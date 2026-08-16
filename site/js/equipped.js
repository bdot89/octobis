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
 * Picks a full set greedily, slot by slot, skipping anything already used.
 *
 * Greedy is the honest choice here: a true optimiser would need to model set bonuses, and those
 * are not scored (see the README), so a more elaborate search would be false precision.
 */
export function autoFill(data, character, spec, classDef, phaseId, overrides) {
  const equipped = {};
  const used = new Set();

  for (const slot of ALL_SLOTS) {
    // A two-hander already occupies the off hand.
    if (slot.key === 'offhand' && isTwoHanded(data.byId.get(equipped.mainhand))) continue;

    const candidates = candidatesFor(data, character, spec, classDef, phaseId, slot.key, overrides);
    const pick = candidates.find(c => c.score > 0 && !used.has(c.item.id));
    if (!pick) continue;

    equipped[slot.key] = pick.item.id;
    used.add(pick.item.id);
  }

  return equipped;
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
 * Adds up everything the equipped set contributes.
 *
 * Only gear is counted. Base class and race values are not in the dataset, so showing a total
 * "Health" or "Mana" figure would mean inventing the larger half of the number.
 */
export function summarise(data, gear, spec, overrides, enchantStats = {}) {
  const totals = {};
  let score = 0;
  let equippedCount = 0;

  // Enchant stats are part of what you are wearing, so they belong in the totals.
  for (const [key, value] of Object.entries(enchantStats)) {
    totals[key] = (totals[key] ?? 0) + value;
  }

  for (const [slotKey, itemId] of Object.entries(gear)) {
    const item = data.byId.get(itemId);
    if (!item) continue;

    equippedCount++;
    const slot = slotByKey(slotKey);
    score += scoreItem(item, spec, overrides, slot ? contextFor(slot) : 'armor');

    for (const [key, value] of Object.entries(item.stats)) {
      totals[key] = (totals[key] ?? 0) + value;
    }
  }

  const groups = SUMMARY_GROUPS
    .map(group => ({
      ...group,
      entries: group.stats
        .filter(([key]) => totals[key])
        .map(([key, label]) => ({ key, label, value: round(totals[key]) }))
    }))
    .filter(group => group.entries.length > 0);

  return { totals, groups, score: Math.round(score), equippedCount };
}

function round(value) {
  return Math.round(value * 100) / 100;
}
