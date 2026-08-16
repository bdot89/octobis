// Enchants. The list of what exists comes from the Atlas addon; what each one is worth comes from
// config/enchants.json.
//
// Enchants are not phase-gated. An enchant you can apply is available whatever raid tier the server
// is on, so every slot offers the full list in every phase.

import { isTwoHanded } from './slots.js';

/** Every slot can take something: enchants, a belt buckle, or a gem on a ring or amulet. */
export const ENCHANTABLE = new Set([
  'head', 'back', 'chest', 'wrist', 'hands', 'waist', 'legs', 'feet',
  'neck', 'finger1', 'finger2', 'mainhand', 'offhand'
]);

const KIND_LABELS = { enchant: 'Enchant', buckle: 'Belt buckle', gem: 'Gem' };

/**
 * Builds the per-slot lookup.
 *
 * Stats come from the scrape itself - the database states each effect in words and the scraper
 * parses them - so config is only needed to correct or annotate individual entries.
 */
export function buildIndex(imported, config = {}) {
  const bySlot = new Map();

  const add = (slot, entry) => {
    if (!bySlot.has(slot)) bySlot.set(slot, []);
    bySlot.get(slot).push(entry);
  };

  for (const enchant of imported?.enchants ?? []) {
    const override = config.stats?.[enchant.name];
    const stats = cleanStats(override ?? enchant.stats);
    const hasStats = Object.keys(stats).length > 0;

    for (const slot of enchant.slots) {
      add(slot, {
        key: `${enchant.kind?.[0] ?? 'e'}${enchant.id}`,
        name: enchant.name,
        kind: enchant.kind ?? 'enchant',
        source: KIND_LABELS[enchant.kind] ?? 'Enchant',
        stats,
        effect: enchant.effect ?? null,
        note: override?.note ?? null,
        proc: Boolean(enchant.proc),
        // Unscored means "applied but contributing nothing", which the UI says out loud rather
        // than letting it look like a zero-value choice.
        unscored: !hasStats,
        twoHandOnly: Boolean(enchant.twoHandOnly),
        shieldOnly: Boolean(enchant.shieldOnly)
      });
    }
  }

  for (const list of bySlot.values()) {
    // Scored entries first - they are what most people are looking for - then alphabetical.
    list.sort((a, b) => (a.unscored === b.unscored ? a.name.localeCompare(b.name) : a.unscored ? 1 : -1));
  }

  return bySlot;
}

/** Strips the bookkeeping keys so only real stats remain. */
function cleanStats(stats) {
  if (!stats) return {};
  const out = {};
  for (const [key, value] of Object.entries(stats)) {
    if (key === 'note' || key === '_unscored') continue;
    if (typeof value === 'number' && value !== 0) out[key] = value;
  }
  return out;
}

/**
 * Enchants offered for a slot, filtered by what is actually equipped there - a shield enchant is
 * only an option if you are holding a shield, and a two-hander cannot take a one-hand enchant.
 */
export function forSlot(index, slotKey, equippedItem) {
  const list = index.get(slotKey) ?? [];
  if (slotKey !== 'mainhand' && slotKey !== 'offhand') return list;

  const twoHanded = isTwoHanded(equippedItem);
  const isShield = equippedItem?.type === 'shield';

  return list.filter(e => {
    if (e.shieldOnly) return slotKey === 'offhand' && isShield;
    if (e.twoHandOnly) return slotKey === 'mainhand' && twoHanded;
    // Ordinary weapon enchants do not go on a shield.
    if (slotKey === 'offhand' && isShield) return false;
    if (slotKey === 'mainhand' && twoHanded) return false;
    return true;
  });
}

export function find(index, slotKey, key) {
  return (index.get(slotKey) ?? []).find(e => e.key === key) ?? null;
}

/** Combined stats from every applied enchant, for folding into the character totals. */
export function appliedStats(index, applied) {
  const totals = {};

  for (const [slotKey, key] of Object.entries(applied ?? {})) {
    const enchant = find(index, slotKey, key);
    if (!enchant) continue;
    for (const [stat, value] of Object.entries(enchant.stats)) {
      totals[stat] = (totals[stat] ?? 0) + value;
    }
  }

  return totals;
}

/** Hit contributed by enchants, so the guide can count it alongside gear and talents. */
export function appliedHit(index, applied, kind) {
  const statKey = kind === 'spell' ? 'spellHit' : 'hit';
  const pieces = [];

  for (const [slotKey, key] of Object.entries(applied ?? {})) {
    const enchant = find(index, slotKey, key);
    const value = enchant?.stats?.[statKey];
    if (!value) continue;
    pieces.push({ slotKey, name: enchant.name, value });
  }

  return { total: pieces.reduce((sum, p) => sum + p.value, 0), pieces };
}
