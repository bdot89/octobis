// Everything about the currently equipped set: which items fit a slot, how they rank, what the
// set adds up to, and what auto-fill should choose.

import { canUse, scoreItem, availableIn } from './score.js';
import { ALL_SLOTS, contextFor, isTwoHanded, slotByKey } from './slots.js';

/**
 * Every item that could go in a slot for this character and phase, best first.
 * Score is left raw here; the UI normalises it against the best entry so the top item reads 100.
 */
export function candidatesFor(data, character, spec, classDef, phaseId, slotKey, overrides) {
  const slot = slotByKey(slotKey);
  if (!slot) return [];

  const invTypes = new Set(slot.invTypes);
  const context = contextFor(slot);

  return data.items
    .filter(item =>
      invTypes.has(item.slot) &&
      availableIn(item, phaseId) &&
      !overrides.exclude.has(String(item.id)) &&
      canUse(item, classDef))
    .map(item => ({
      item,
      score: scoreItem(item, spec, overrides, context),
      sources: data.sources[String(item.id)] ?? [],
      note: overrides.note[item.id] ?? null,
      adjusted: Boolean(overrides.bonus[item.id] || overrides.note[item.id])
    }))
    .sort((a, b) => b.score - a.score || a.item.name.localeCompare(b.item.name));
}

/** Turns raw scores into the 0-100 scale the list displays, relative to the best candidate. */
export function normalise(candidates) {
  const best = candidates.reduce((max, c) => Math.max(max, c.score), 0);
  if (best <= 0) return candidates.map(c => ({ ...c, rating: 0 }));
  return candidates.map(c => ({ ...c, rating: Math.max(0, Math.round((c.score / best) * 100)) }));
}

/**
 * Picks a full set for the character, in three passes.
 *
 * Hit is not an ordinary stat. It is a *requirement* with a hard ceiling: below the cap you are
 * missing attacks outright, above it every point is dead weight. Scoring it linearly cannot express
 * that - a weight high enough to reach the cap overvalues hit everywhere else, and a weight low
 * enough to be fair leaves the set short. So the cap is handled as a constraint instead:
 *
 *   1. greedy   - best item per slot, with hit past the cap valued at nothing
 *   2. gap pass - buy the missing hit, cheapest score sacrifice first, until the cap is met
 *   3. refine   - re-optimise everything else without giving that hit back
 *
 * Talents and enchants are counted before a single item is chosen (they arrive in
 * `hitBudget.alreadyHave`), so a Shadow priest with 5/5 Shadow Focus shops for the remaining 6%
 * rather than the full 16%, and a build with no hit talents shops for all of it.
 */
export function autoFill(data, character, spec, classDef, phaseId, overrides, hitBudget = null) {
  const equipped = {};
  const used = new Set();

  const statKey = hitBudget?.statKey ?? null;
  const weight = statKey ? (spec.weights?.[statKey] ?? 0) : 0;

  // Hit already guaranteed by talents and enchants counts against the budget from the start.
  let hitSoFar = hitBudget?.alreadyHave ?? 0;
  const cap = hitBudget?.cap ?? Infinity;

  const pool = candidateCache(data, character, spec, classDef, phaseId, overrides);

  for (const slot of ALL_SLOTS) {
    // A two-hander already occupies the off hand.
    if (slot.key === 'offhand' && isTwoHanded(data.byId.get(equipped.mainhand))) continue;

    const candidates = pool(slot.key).filter(c => !used.has(c.item.id));
    if (candidates.length === 0) continue;

    const valued = statKey
      ? candidates.map(c => {
          const own = c.item.stats?.[statKey] ?? 0;
          const surplus = Math.max(0, hitSoFar + own - cap);
          const wasted = Math.min(own, surplus);
          // Surplus hit is worth nothing here. Auto-fill treats the cap as a hard budget so it
          // stops at it rather than piling on to 17% against a 9% cap, which is what a per-item
          // cap allows - each piece looks fine on its own and the total runs away.
          return { ...c, score: c.score - wasted * weight };
        })
      : candidates;

    let pick = valued.reduce((best, c) => (best === null || c.score > best.score ? c : best), null);

    // A slot where nothing scores is not a slot to leave empty. Relics carry no stats at all, so
    // every one of them scores zero unless overrides.json ranks it, and the Ranged / Relic slot sat
    // empty for Paladin, Shaman and Druid. Where the engine has no opinion, take the best item on
    // the plain facts - quality, then item level - rather than nothing at all.
    if ((!pick || pick.score <= 0) && !valued.some(c => c.score > 0)) {
      pick = valued.reduce((best, c) => (best === null || better(c.item, best.item) ? c : best), null);
    }

    if (!pick) continue;

    equipped[slot.key] = pick.item.id;
    used.add(pick.item.id);
    hitSoFar += pick.item.stats?.[statKey] ?? 0;
  }

  if (!statKey) return equipped;

  const secured = closeHitGap(data, spec, overrides, equipped, hitBudget, pool);
  return refine(data, spec, overrides, equipped, hitBudget, secured, pool);
}

/** Item inventory types, for the weapon-style rules below. */
const TWO_HAND = 17;
const WEAPON_SLOTS = new Set([13, 21, 22]);   // one-hand, main hand, off hand

/** Shields arrive as inventory type 22 ("Off Hand"); the subclass is what identifies one. */
const isShield = item => item.type === 'shield' || item.slot === 14;

/** Held-in-off-hand: tomes, orbs and the like. Not a weapon, so anyone may hold one. */
const HELD_IN_OFF_HAND = 23;

/**
 * Whether the spec's weapon style permits this item in this slot.
 *
 * A Protection warrior holding a two-hander is not a Protection warrior: block, block value and
 * every shield talent stop working, and the off hand sits empty. The BiS list has always resolved
 * the pairing properly, but auto-fill filled each slot on its own merits and handed all four shield
 * specs a two-hander - and now a shaman tank, whose Spirit Armor and Shield Specialization do
 * nothing at all without one.
 *
 * The picker is deliberately left alone: browsing every option for a slot is useful even when
 * auto-fill would not choose it.
 */
export function allowedByStyle(slot, item, spec) {
  const style = spec.weaponStyle ?? 'stats';

  if (slot.key === 'mainhand') {
    if (style === 'onehandshield' || style === 'onehandoffhand' || style === 'dualwield') {
      return item.slot !== TWO_HAND;
    }
    return true;
  }

  if (slot.key === 'offhand') {
    if (style === 'onehandshield') return isShield(item);
    // A shield or a held item, never a second weapon: Shamans cannot dual-wield on OctoWoW.
    if (style === 'onehandoffhand') return isShield(item) || item.slot === HELD_IN_OFF_HAND;
    if (style === 'dualwield') return WEAPON_SLOTS.has(item.slot) && !isShield(item);
    // Casters and hunters: a held item or a shield, but not a weapon they cannot swing.
    return !WEAPON_SLOTS.has(item.slot) || isShield(item);
  }

  return true;
}

/** Candidate lists are reused across every pass and every round, so they are only built once. */
function candidateCache(data, character, spec, classDef, phaseId, overrides) {
  const cache = new Map();
  return slotKey => {
    if (!cache.has(slotKey)) {
      const slot = slotByKey(slotKey);
      const all = candidatesFor(data, character, spec, classDef, phaseId, slotKey, overrides);
      const usable = slot ? all.filter(c => allowedByStyle(slot, c.item, spec)) : all;
      // Never leave a slot with nothing to choose from: a style rule that no item can satisfy is a
      // gap in the data, and an empty hand is worse than an imperfect one.
      cache.set(slotKey, usable.length > 0 ? usable : all);
    }
    return cache.get(slotKey);
  };
}

/**
 * Fallback ordering when nothing can be scored: the plain facts on the item.
 *
 * Item level would be the natural tiebreak but the database does not put it in the tooltip, so it
 * is 0 on all but four items and sorts nothing. Required level is present and carries most of the
 * same signal.
 */
function better(a, b) {
  if (a.quality !== b.quality) return a.quality > b.quality;
  if ((a.ilvl ?? 0) !== (b.ilvl ?? 0)) return (a.ilvl ?? 0) > (b.ilvl ?? 0);
  if ((a.req ?? 0) !== (b.req ?? 0)) return (a.req ?? 0) > (b.req ?? 0);
  return a.name.localeCompare(b.name) < 0;
}

/** Total hit the set supplies, on top of whatever talents and enchants already give. */
function totalHit(data, gear, statKey, base) {
  let sum = base;
  for (const id of Object.values(gear)) sum += data.byId.get(id)?.stats?.[statKey] ?? 0;
  return round(sum);
}

/**
 * Buys the hit still missing after the greedy pass, one swap at a time.
 *
 * Each swap is judged on its exchange rate - score given up per point of hit gained - and the
 * cheapest is taken, so the set reaches the cap by sacrificing as little as it can. Hit past the
 * cap is not what is being bought and does not count towards the trade, which stops a single
 * enormous-hit piece from looking like a bargain when only 1% is still needed.
 *
 * Returns the hit actually secured, which is the cap when it is reachable and the best available
 * total when it is not - a Launch-phase caster cannot find 16% spell hit at all, and pretending
 * otherwise would make the next pass throw away good gear chasing something that does not exist.
 */
function closeHitGap(data, spec, overrides, gear, hitBudget, pool) {
  const { statKey } = hitBudget;
  const cap = hitBudget.cap ?? Infinity;
  const base = hitBudget.alreadyHave ?? 0;
  const MAX_SWAPS = ALL_SLOTS.length * 2;

  for (let swap = 0; swap < MAX_SWAPS; swap++) {
    const deficit = round(cap - totalHit(data, gear, statKey, base));
    if (deficit <= 0) break;

    const inUse = new Set(Object.values(gear));
    let best = null;

    for (const slot of ALL_SLOTS) {
      if (slot.key === 'offhand' && isTwoHanded(data.byId.get(gear.mainhand))) continue;

      const currentId = gear[slot.key];
      const current = currentId ? data.byId.get(currentId) : null;
      const currentHit = current?.stats?.[statKey] ?? 0;
      const currentScore = current ? scoreItem(current, spec, overrides, contextFor(slot)) : 0;

      for (const candidate of pool(slot.key)) {
        if (candidate.item.id !== currentId && inUse.has(candidate.item.id)) continue;
        if (!keepsHandedness(slot, current, candidate.item)) continue;

        const gain = (candidate.item.stats?.[statKey] ?? 0) - currentHit;
        if (gain <= 0) continue;

        const useful = Math.min(gain, deficit);
        const cost = currentScore - candidate.score;   // positive means we are giving something up
        const rate = cost / useful;                    // score paid per point of hit gained

        if (best === null || rate < best.rate) best = { slotKey: slot.key, item: candidate.item, rate };
      }
    }

    if (best === null) break;   // nothing available in this phase can add any more hit
    gear[best.slotKey] = best.item.id;
  }

  return Math.min(cap, totalHit(data, gear, statKey, base));
}

/**
 * Second pass over the set.
 *
 * Going slot by slot means early choices are made before the hit budget is spent, so a piece taken
 * for "free" hit in the first slot can still be there once the budget is full and its hit is worth
 * nothing. This revisits every slot knowing the whole set, and keeps swapping while something
 * improves - which is the same comparison the guide's replacement advice makes.
 *
 * `secured` is the hit the gap pass managed to reach. No swap here is allowed to fall below it,
 * so re-optimising for damage can never quietly hand back the cap.
 */
function refine(data, spec, overrides, equipped, hitBudget, secured, pool) {
  const { statKey } = hitBudget;
  const cap = hitBudget.cap ?? Infinity;
  const weight = spec.weights?.[statKey] ?? 0;
  const baseHit = hitBudget.alreadyHave ?? 0;

  const gear = { ...equipped };
  const MAX_ROUNDS = 6;

  for (let pass = 0; pass < MAX_ROUNDS; pass++) {
    let improved = false;

    for (const slot of ALL_SLOTS) {
      const currentId = gear[slot.key];
      if (currentId === undefined) continue;
      if (slot.key === 'offhand' && isTwoHanded(data.byId.get(gear.mainhand))) continue;

      const inUse = new Set(Object.entries(gear).filter(([k]) => k !== slot.key).map(([, id]) => id));

      // Hit the rest of the set already supplies, so this slot is judged against the true total.
      let hitElsewhere = baseHit;
      for (const id of inUse) hitElsewhere += data.byId.get(id)?.stats?.[statKey] ?? 0;

      const context = contextFor(slot);
      const value = item => {
        const own = item.stats?.[statKey] ?? 0;
        const wasted = Math.min(own, Math.max(0, hitElsewhere + own - cap));
        return scoreItem(item, spec, overrides, context) - wasted * weight;
      };

      const current = data.byId.get(currentId);
      const currentValue = value(current);

      let best = null;
      for (const candidate of pool(slot.key)) {
        if (inUse.has(candidate.item.id)) continue;
        if (!keepsHandedness(slot, current, candidate.item)) continue;
        // Never give back hit the gap pass had to pay for.
        if (hitElsewhere + (candidate.item.stats?.[statKey] ?? 0) < secured) continue;

        const score = value(candidate.item);
        if (score > currentValue && (best === null || score > best.score)) best = { item: candidate.item, score };
      }

      if (best) {
        gear[slot.key] = best.item.id;
        improved = true;
      }
    }

    if (!improved) break;
  }

  return gear;
}

/**
 * Swapping a two-hander for a one-hander would leave the off hand empty, and the reverse would
 * leave two items in one pair of hands. Neither pass re-plans the other slot, so both are refused.
 */
function keepsHandedness(slot, current, candidate) {
  if (slot.key !== 'mainhand' || !current) return true;
  return isTwoHanded(current) === isTwoHanded(candidate);
}

const SUMMARY_GROUPS = [
  {
    id: 'primary', name: 'Primary',
    stats: [['str', 'Strength'], ['agi', 'Agility'], ['sta', 'Stamina'], ['int', 'Intellect'], ['spi', 'Spirit']]
  },
  {
    id: 'offensive', name: 'Offensive',
    stats: [
      ['ap', 'Attack Power'], ['rap', 'Ranged Attack Power'], ['spellPower', 'Spell Damage'],
      ['healPower', 'Healing Power'], ['crit', 'Crit %'], ['hit', 'Hit %'],
      ['spellCrit', 'Spell Crit %'], ['spellHit', 'Spell Hit %'], ['haste', 'Attack Speed %'],
      ['armorPen', 'Armor Penetration'], ['spellPen', 'Spell Penetration'],
      ['flatMeleeDamage', 'Bonus Weapon Damage'], ['apFeral', 'Attack Power (Cat/Bear forms)'], ['apVsTarget', 'Attack Power (vs creature type)'],
      ['spellDmgArcane', 'Arcane Damage'], ['spellDmgFire', 'Fire Damage'],
      ['spellDmgFrost', 'Frost Damage'], ['spellDmgHoly', 'Holy Damage'],
      ['spellDmgNature', 'Nature Damage'], ['spellDmgShadow', 'Shadow Damage']
    ]
  },
  {
    id: 'sustain', name: 'Sustain',
    stats: [['mp5', 'Mana per 5'], ['hp5', 'Health per 5'], ['mp5WhileCasting', 'Mana Regen While Casting %'], ['leech', 'Leech %']]
  },
  {
    id: 'defensive', name: 'Defensive',
    stats: [
      ['armor', 'Armor'], ['defense', 'Defense'], ['dodge', 'Dodge %'], ['parry', 'Parry %'],
      ['block', 'Block %'], ['blockValue', 'Block Value'],
      ['resArcane', 'Arcane Resistance'], ['resFire', 'Fire Resistance'],
      ['resFrost', 'Frost Resistance'], ['resNature', 'Nature Resistance'],
      ['resShadow', 'Shadow Resistance']
    ]
  }
];

/**
 * Adds up everything the character contributes: gear, enchants, and any talent that grants a stat
 * outright. Talent hit is the one that matters in practice - reading "2% spell hit" on a Shadow
 * priest holding 5/5 Shadow Focus is simply wrong, because the character has 12%.
 *
 * Only these three are counted. Base class and race values are not in the dataset, so showing a
 * total "Health" or "Mana" figure would mean inventing the larger half of the number.
 */
export function summarise(data, gear, spec, overrides, enchantStats = {}, talentStats = {}) {
  const totals = {};
  const fromGear = {};
  let score = 0;
  let equippedCount = 0;

  // Enchant and talent stats are part of what the character has, so they belong in the totals -
  // but not in the gear score, which is a measure of the equipment alone.
  for (const source of [enchantStats, talentStats]) {
    for (const [key, value] of Object.entries(source)) {
      totals[key] = (totals[key] ?? 0) + value;
    }
  }

  for (const [slotKey, itemId] of Object.entries(gear)) {
    const item = data.byId.get(itemId);
    if (!item) continue;

    equippedCount++;
    const slot = slotByKey(slotKey);
    score += scoreItem(item, spec, overrides, slot ? contextFor(slot) : 'armor');

    for (const [key, value] of Object.entries(item.stats)) {
      totals[key] = (totals[key] ?? 0) + value;
      fromGear[key] = (fromGear[key] ?? 0) + value;
    }
  }

  const groups = SUMMARY_GROUPS
    .map(group => ({
      ...group,
      entries: group.stats
        .filter(([key]) => totals[key])
        .map(([key, label]) => ({
          key,
          label,
          value: round(totals[key]),
          // Only worth spelling out when something other than gear contributed.
          breakdown: talentStats[key]
            ? `${round(fromGear[key] ?? 0) + round(enchantStats[key] ?? 0)} gear · ${round(talentStats[key])} talents`
            : null
        }))
    }))
    .filter(group => group.entries.length > 0);

  return { totals, fromGear, groups, score: Math.round(score), equippedCount };
}

function round(value) {
  return Math.round(value * 100) / 100;
}
