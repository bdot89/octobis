// The ranking engine.
//
// Everything here is derived at page load from config/specs.json and the scraped stats, so editing
// a stat weight and reloading is all it takes to re-rank the whole site.

import { sourcesFor } from './data.js';

/** Inventory types where the armour-type restriction actually applies. */
const ARMOR_RESTRICTED_SLOTS = new Set([1, 3, 5, 6, 7, 8, 9, 10, 20]);
const ARMOR_TYPES = new Set(['cloth', 'leather', 'mail', 'plate']);
const RELIC_TYPES = new Set(['libram', 'idol', 'totem']);

const MAIN_HAND = new Set([13, 21]);
const OFF_HAND_WEAPON = new Set([13, 22]);
const SHIELD = 14;

/**
 * The database labels a shield's slot "Off Hand", so shields arrive as inventory type 22 rather
 * than 14. The subclass is what actually identifies one.
 */
function isShield(item) {
  return item.type === 'shield' || item.slot === SHIELD;
}
const HELD_IN_OFF_HAND = 23;
const TWO_HAND = 17;
const RANGED_SLOTS = new Set([15, 25, 26, 28]);

const ALTERNATIVES = 4;

/**
 * Is this item obtainable by the given phase?
 *
 * The single gate every view goes through. An item whose phase could not be established has
 * `minPhase === null` and is never treated as available: guessing "available now" once put
 * Naxxramas and Ahn'Qiraj gear into the launch planner, and silently offering an item you cannot
 * get is worse than not listing it.
 */
export function availableIn(item, phaseId) {
  return item.minPhase !== null && item.minPhase !== undefined && item.minPhase <= phaseId;
}

/** Can this class physically equip the item? */
export function canUse(item, classDef) {
  if (item.classes.length && !item.classes.includes(classDef.id)) return false;

  if (item.cls === 4) {
    const type = item.type;
    if (type === 'shield') return Boolean(classDef.weapons.shield);
    if (RELIC_TYPES.has(type)) return classDef.ranged.includes(type);

    // Cloaks are cloth and rings are miscellaneous, but neither is armour-type restricted, so the
    // check only bites on slots that actually have an armour class.
    if (ARMOR_TYPES.has(type) && ARMOR_RESTRICTED_SLOTS.has(item.slot)) {
      return classDef.armor.includes(type);
    }
    return true;
  }

  if (item.cls === 2) {
    const type = item.type;
    if (RANGED_SLOTS.has(item.slot)) return classDef.ranged.includes(type);
    if (item.slot === TWO_HAND) return classDef.weapons.twoHand.includes(type);
    return classDef.weapons.oneHand.includes(type);
  }

  return true;
}

/**
 * Collapses school-specific spell damage into spell power for the spec's own school, and expands
 * "all resistances" into the individual schools, so the weight table only needs generic keys.
 */
function effectiveStats(item, spec, context) {
  const stats = { ...item.stats };

  if (spec.school) {
    const schoolKey = 'spellDmg' + spec.school[0].toUpperCase() + spec.school.slice(1);
    if (stats[schoolKey]) {
      stats.spellPower = (stats.spellPower ?? 0) + stats[schoolKey];
    }
  }
  // Off-school damage never contributes; drop every school key now that ours has been folded in.
  for (const key of Object.keys(stats)) if (key.startsWith('spellDmg')) delete stats[key];

  if (stats.resAll) {
    for (const key of ['resFire', 'resFrost', 'resNature', 'resShadow', 'resArcane']) {
      stats[key] = (stats[key] ?? 0) + stats.resAll;
    }
    delete stats.resAll;
  }

  // Weapon damage only counts in the hand it is actually swung in.
  if (context === 'ranged') {
    stats.rangedDps = stats.weaponDps ?? 0;
    delete stats.weaponDps;
  } else if (context === 'armor') {
    delete stats.weaponDps;
  } else if (context === 'offhand' && stats.weaponDps) {
    // 1.12 halves off-hand weapon damage.
    stats.weaponDps *= 0.5;
  }

  return stats;
}

/**
 * Scores one item.
 *
 * There is deliberately no per-item cap here. A cap is a property of the whole set - no single item
 * carries 9% hit, or 100 defense - so a per-item cap can never fire, and all 23 of them were dead
 * code that made the BiS list look hit-aware while doing nothing. Hit is handled where it belongs,
 * as a set-level budget: see `hitAdjusted` below and the three passes in equipped.js.
 */
export function scoreItem(item, spec, overrides, context = 'armor') {
  const weights = spec.weights ?? {};
  const stats = effectiveStats(item, spec, context);

  let total = 0;
  for (const [key, value] of Object.entries(stats)) {
    const weight = weights[key];
    if (!weight || !value) continue;
    total += value * weight;
  }

  return total + (overrides.bonus[item.id] ?? 0);
}

/**
 * The value of an item once the hit already on the rest of the set is taken into account.
 *
 * This is the same trade auto-fill makes, so the BiS list and the planner agree. Hit past the cap
 * is worth nothing; hit below it keeps full weight. Without a budget the two tabs disagreed - Beast
 * Mastery's Launch list totalled 18% hit against an 8% cap while auto-fill landed exactly on it.
 *
 * `whiteCap` models dual wielding: white swings need far more hit than specials, so surplus past the
 * yellow cap is not dead for those specs, only worth less. A single cliff at the yellow cap is
 * wrong for Fury, Combat, Assassination and Subtlety.
 */
export function hitAdjusted(score, item, spec, budget, hitElsewhere) {
  if (!budget) return score;

  const { statKey, cap, whiteCap = null, whiteWeight = 0.5 } = budget;
  const own = item.stats?.[statKey] ?? 0;
  if (!own) return score;

  const weight = spec.weights?.[statKey] ?? 0;
  if (!weight) return score;

  const overYellow = Math.min(own, Math.max(0, hitElsewhere + own - cap));
  if (overYellow <= 0) return score;

  // Beyond the yellow cap, a dual-wielder still gains until white swings are capped too.
  const stillUseful = whiteCap
    ? Math.min(overYellow, Math.max(0, whiteCap - Math.max(cap, hitElsewhere)))
    : 0;

  const dead = overYellow - stillUseful;
  return score - dead * weight - stillUseful * weight * (1 - whiteWeight);
}

function decorate(data, item, score, overrides) {
  return {
    item,
    score,
    sources: sourcesFor(data, item.id),
    note: overrides.note[item.id] ?? null,
    adjusted: Boolean(overrides.bonus[item.id] || overrides.note[item.id] || overrides.pin[item.id])
  };
}

function rank(data, candidates, spec, overrides, context, slotId, take, hit = null) {
  // A pin jumps to the front of its slot regardless of what the maths thinks.
  const pinnedIds = Object.entries(overrides.pin)
    .filter(([, target]) => target === slotId)
    .map(([id]) => Number(id));

  // The score filter has to spare pinned items, or pinning is a silent no-op for exactly the
  // items most likely to be pinned: relics and proc trinkets, which score zero by definition.
  const scored = candidates
    .map(item => decorate(
      data, item,
      hitAdjusted(scoreItem(item, spec, overrides, context), item, spec, hit?.budget, hit?.elsewhere ?? 0),
      overrides))
    .filter(entry => entry.score > 0 || pinnedIds.includes(entry.item.id))
    .sort((a, b) => b.score - a.score);

  if (pinnedIds.length) {
    const pinned = pinnedIds
      .map(id => scored.find(entry => entry.item.id === id))
      .filter(Boolean);
    const rest = scored.filter(entry => !pinnedIds.includes(entry.item.id));
    return [...pinned, ...rest].slice(0, take);
  }

  return scored.slice(0, take);
}

/**
 * Chooses between a two-handed weapon and a one-hand pairing by total score, then reports whichever
 * won as a set of rows. Set bonuses aside, this is the one place where slots genuinely interact.
 */
function resolveWeapons(data, pool, classDef, spec, overrides, hit = null) {
  const style = spec.weaponStyle ?? 'stats';
  const pick = list => (list.length ? list[0] : null);

  const rankFrom = (predicate, slotId, context = 'melee') =>
    rank(data, pool.filter(predicate), spec, overrides, context, slotId, ALTERNATIVES + 2, hit);

  const twoHanders = rankFrom(i => i.slot === TWO_HAND, 'weapon');
  const mainHands = rankFrom(i => MAIN_HAND.has(i.slot), 'weapon');

  // Only a spec that actually dual-wields may put a weapon in the off hand. Letting the "stats"
  // style take one paired Fang of the Mystics with Aurastone Hammer on an Elemental shaman - two
  // one-handers on a class that cannot swing both. Casters take a held item or a shield.
  const offCandidates = i => {
    if (style === 'dualwield') return OFF_HAND_WEAPON.has(i.slot);
    if (style === 'onehandshield' || style === 'onehandoffhand') return isShield(i) || i.slot === HELD_IN_OFF_HAND;
    if (style === 'twohand') return false;
    return i.slot === HELD_IN_OFF_HAND || isShield(i);
  };
  const offHands = rankFrom(offCandidates, 'offhand', 'offhand');

  const bestTwoHand = pick(twoHanders);
  const bestMain = pick(mainHands);

  // A dual-wielder's off hand must not be the same item it is already holding in its main hand.
  const offHandsUsable = bestMain
    ? offHands.filter(entry => entry.item.id !== bestMain.item.id)
    : offHands;
  const bestOff = pick(offHandsUsable);

  const twoHandScore = bestTwoHand?.score ?? -Infinity;
  const pairScore = (bestMain?.score ?? -Infinity) + (bestOff?.score ?? 0);

  const canTwoHand = classDef.weapons.twoHand.length > 0;
  const useTwoHand = canTwoHand && twoHandScore > pairScore;

  if (useTwoHand) {
    return [{ id: 'weapon', name: 'Two-Hand', picks: twoHanders.slice(0, ALTERNATIVES + 1), emptyReason: null }];
  }

  const mainPicks = mainHands.slice(0, ALTERNATIVES + 1);
  const rows = [{
    id: 'weapon',
    name: 'Main Hand',
    picks: mainPicks,
    emptyReason: mainPicks.length ? null : emptyReason(pool.filter(i => MAIN_HAND.has(i.slot)).length)
  }];

  if (bestOff) {
    rows.push({ id: 'offhand', name: offHandName(style), picks: offHandsUsable.slice(0, ALTERNATIVES + 1), emptyReason: null });
  }
  return rows;
}

function emptyReason(candidateCount) {
  return candidateCount === 0
    ? 'Nothing this class can equip has been indexed for this slot yet.'
    : `${candidateCount} candidate${candidateCount === 1 ? '' : 's'} found, but none carry stats this spec values — likely proc or on-use effects, which are not scored. Add a bonus in overrides.json to rank them.`;
}

function offHandName(style) {
  if (style === 'onehandshield') return 'Shield';
  if (style === 'dualwield') return 'Off Hand';
  return 'Off Hand / Shield';
}

/**
 * Builds the whole BiS table for one class, spec and phase.
 * Returns a list of rows, each with the winning pick first and its alternatives behind it.
 */
export function buildBis(data, classDef, spec, phaseId, overrides, hitBudget = null, hitBySlot = {}) {
  const pool = data.items.filter(item =>
    availableIn(item, phaseId) &&
    !overrides.exclude.has(String(item.id)) &&
    canUse(item, classDef));

  const rows = [];

  // Hit already supplied by the rest of the set, so each slot is judged on the true total - the
  // same comparison auto-fill's refine pass makes. Without it this tab ranked without any hit
  // awareness at all while the planner had a budget, and the two disagreed.
  const totalHit = Object.values(hitBySlot).reduce((sum, v) => sum + v, 0);
  const elsewhere = slotId => totalHit - (hitBySlot[slotId] ?? 0);

  for (const slot of data.slots) {
    if (slot.special === 'weapon') {
      rows.push(...resolveWeapons(data, pool, classDef, spec, overrides,
        hitBudget ? { budget: hitBudget, elsewhere: elsewhere('weapon') } : null));
      continue;
    }

    const context = slot.id === 'ranged' ? 'ranged' : 'armor';
    const invTypes = new Set(slot.invTypes);
    const candidates = pool.filter(item => invTypes.has(item.slot));
    const count = slot.count ?? 1;

    const ranked = rank(data, candidates, spec, overrides, context, slot.id, count + ALTERNATIVES,
      hitBudget ? { budget: hitBudget, elsewhere: elsewhere(slot.id) } : null);

    // The slot is kept even when nothing scores, so a spec whose trinkets are all proc-only shows
    // an explained gap rather than silently missing a row.
    rows.push({
      id: slot.id,
      name: slot.name,
      picks: ranked,
      equipped: count > 1 ? count : undefined,
      emptyReason: ranked.length ? null : emptyReason(candidates.length)
    });
  }

  return rows;
}

/** Total score of the winning configuration, used only to compare phases at a glance. */
export function totalScore(rows) {
  return rows.reduce((sum, row) => {
    const equipped = row.equipped ?? 1;
    return sum + row.picks.slice(0, equipped).reduce((s, p) => s + p.score, 0);
  }, 0);
}
