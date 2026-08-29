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
  }

  return stats;
}

export function scoreItem(item, spec, overrides, context = 'armor') {
  const weights = spec.weights ?? {};
  const caps = spec.caps ?? {};
  const stats = effectiveStats(item, spec, context);

  let total = 0;
  for (const [key, value] of Object.entries(stats)) {
    const weight = weights[key];
    if (!weight || !value) continue;

    const cap = caps[key];
    if (cap) {
      const underCap = Math.min(value, cap.cap);
      const overCap = Math.max(0, value - cap.cap);
      total += underCap * weight + overCap * (cap.postCapWeight ?? 0);
    } else {
      total += value * weight;
    }
  }

  return total + (overrides.bonus[item.id] ?? 0);
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

function rank(data, candidates, spec, overrides, context, slotId, take) {
  const scored = candidates
    .map(item => decorate(data, item, scoreItem(item, spec, overrides, context), overrides))
    .filter(entry => entry.score > 0)
    .sort((a, b) => b.score - a.score);

  // A pin jumps to the front of its slot regardless of what the maths thinks.
  const pinnedIds = Object.entries(overrides.pin)
    .filter(([, target]) => target === slotId)
    .map(([id]) => Number(id));

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
function resolveWeapons(data, pool, classDef, spec, overrides) {
  const style = spec.weaponStyle ?? 'stats';
  const pick = list => (list.length ? list[0] : null);

  const context = 'melee';
  const rankFrom = (predicate, slotId) =>
    rank(data, pool.filter(predicate), spec, overrides, context, slotId, ALTERNATIVES + 2);

  const twoHanders = rankFrom(i => i.slot === TWO_HAND, 'weapon');
  const mainHands = rankFrom(i => MAIN_HAND.has(i.slot), 'weapon');

  const offCandidates = i => {
    if (style === 'dualwield') return OFF_HAND_WEAPON.has(i.slot);
    if (style === 'onehandshield') return isShield(i);
    // Casters and hunters take whatever gives the most stats in the off hand.
    return i.slot === HELD_IN_OFF_HAND || isShield(i) || (style !== 'twohand' && OFF_HAND_WEAPON.has(i.slot));
  };
  const offHands = rankFrom(offCandidates, 'offhand');

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
export function buildBis(data, classDef, spec, phaseId, overrides) {
  const pool = data.items.filter(item =>
    availableIn(item, phaseId) &&
    !overrides.exclude.has(String(item.id)) &&
    canUse(item, classDef));

  const rows = [];

  for (const slot of data.slots) {
    if (slot.special === 'weapon') {
      rows.push(...resolveWeapons(data, pool, classDef, spec, overrides));
      continue;
    }

    const context = slot.id === 'ranged' ? 'ranged' : 'armor';
    const invTypes = new Set(slot.invTypes);
    const candidates = pool.filter(item => invTypes.has(item.slot));
    const count = slot.count ?? 1;

    const ranked = rank(data, candidates, spec, overrides, context, slot.id, count + ALTERNATIVES);

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
