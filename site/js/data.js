// Loads and indexes every JSON file the site needs, and normalises the few places where the
// scraped vocabulary and the config vocabulary do not line up.

import { buildIndex } from './enchants.js';

const FILES = {
  items: './data/items.json',
  sources: './data/sources.json',
  zones: './data/zones.json',
  meta: './data/meta.json',
  phases: './config/phases.json',
  specs: './config/specs.json',
  slots: './config/slots.json',
  overrides: './config/overrides.json',
  races: './config/races.json',
  hitcaps: './config/hitcaps.json',
  enchantConfig: './config/enchants.json',
  buildConfig: './config/builds.json',
  talents: './data/talents.json',
  enchants: './data/enchants.json'
};

async function fetchJson(url) {
  const response = await fetch(url, { cache: 'no-cache' });
  if (!response.ok) throw new Error(`${url} → ${response.status} ${response.statusText}`);
  return response.json();
}

/**
 * The database writes subclass names as they appear in a tooltip ("Fist Weapon", "Cloth"), while
 * the class config uses bare lowercase types ("fist", "cloth"). One place to reconcile them.
 */
export function normaliseType(subName) {
  if (!subName) return null;
  return subName.toLowerCase().replace(/\s*weapon$/, '').trim();
}

const ARMOR_SUBCLASSES = new Set(['cloth', 'leather', 'mail', 'plate', 'shield', 'libram', 'idol', 'totem', 'miscellaneous']);
const WEAPON_SUBCLASSES = new Set(['axe', 'mace', 'sword', 'dagger', 'fist', 'polearm', 'staff', 'bow', 'gun', 'crossbow', 'wand', 'thrown', 'spear']);

/** 4 = armour, 2 = weapon, 0 = unknown, matching the database's own numbering. */
function inferItemClass(type) {
  if (!type) return 0;
  if (ARMOR_SUBCLASSES.has(type)) return 4;
  if (WEAPON_SUBCLASSES.has(type)) return 2;
  return 0;
}

/** Which block of default weights a spec inherits. See the comment in config/specs.json. */
function archetypeOf(spec) {
  if (spec.role === 'healer') return 'healer';
  if (spec.role === 'tank') return 'tank';
  return spec.school ? 'caster' : 'melee';
}

/** Files the site can do without: a missing one degrades a feature rather than the whole page. */
const OPTIONAL = new Set(['talents', 'enchants']);

/** Enchants are optional data; without the file the site simply offers none. */
function buildEnchantIndex(imported, config) {
  return buildIndex(imported, config ?? {});
}

export async function loadAll() {
  const entries = await Promise.all(
    Object.entries(FILES).map(async ([key, url]) => {
      try {
        return [key, await fetchJson(url)];
      } catch (error) {
        if (OPTIONAL.has(key)) return [key, null];
        throw error;
      }
    })
  );
  const raw = Object.fromEntries(entries);

  // Effect sentences are shared: the same wording appears on dozens of items, so the file stores
  // each one once and items reference it by index. Resolved here so nothing downstream has to know.
  const effectTable = raw.items.effects ?? [];

  const items = raw.items.items.map(item => {
    const type = normaliseType(item.subName);
    const { fx, ...rest } = item;
    return {
      ...rest,
      type,
      // Items reached only through their own page carry no item class from a listview. Without one
      // the usability check would wave everything through, so infer it from the tooltip's type.
      cls: item.cls || inferItemClass(type),
      stats: item.stats ?? {},
      classes: item.classes ?? [],
      effects: (fx ?? []).map(i => effectTable[i]).filter(Boolean)
    };
  });

  const byId = new Map(items.map(item => [item.id, item]));

  const defaults = raw.specs.defaults ?? {};
  const classes = raw.specs.classes.map(cls => ({
    ...cls,
    specs: cls.specs.map(spec => ({
      ...spec,
      weights: { ...(defaults[archetypeOf(spec)] ?? {}), ...spec.weights }
    }))
  }));

  return {
    items,
    byId,
    iconBase: raw.items.iconBase,
    iconExtension: raw.items.iconExtension ?? '.png',
    itemUrlBase: raw.items.itemUrlBase,
    sources: raw.sources.sources,
    sets: raw.items.sets ?? {},
    zones: raw.zones.zones,
    meta: raw.meta,
    phases: raw.phases.phases,
    races: raw.races.races,
    factions: raw.races.factions,
    talents: raw.talents,
    hitcaps: raw.hitcaps,
    buildConfig: raw.buildConfig,
    enchantIndex: buildEnchantIndex(raw.enchants, raw.enchantConfig),
    classes,
    slots: raw.slots.slots,
    weaponInvTypes: raw.slots.weaponInvTypes,
    overrides: raw.overrides.scopes ?? {},
    generated: raw.items.generated
  };
}

/**
 * Full URL for an item's icon.
 *
 * 552 items have no art in the database and it serves INV_Misc_QuestionMark for them - almost all
 * of OctoWoW's own additions. The scraper drops that placeholder so the data never claims an icon
 * it does not have, but the slot still needs filling, and the question mark is exactly what the
 * database and the game itself show. An empty box just reads as broken.
 */
export const PLACEHOLDER_ICON = 'inv_misc_questionmark';

export function iconUrl(data, item) {
  if (!item) return null;
  return `${data.iconBase}${item.icon || PLACEHOLDER_ICON}${data.iconExtension}`;
}

/** Sources for an item, newest-phase-last, or an empty list. */
export function sourcesFor(data, itemId) {
  return data.sources[String(itemId)] ?? [];
}

/**
 * Resolves the override entries that apply to a spec in a phase, least specific first so that the
 * more specific scope wins when both name the same item.
 */
export function overridesFor(data, classId, specId, phaseId) {
  const scopes = ['*', classId, `${classId}.${specId}`, `${classId}.${specId}.p${phaseId}`];
  const merged = { pin: {}, exclude: new Set(), bonus: {}, note: {} };

  for (const scope of scopes) {
    const entry = data.overrides[scope];
    if (!entry) continue;
    Object.assign(merged.pin, entry.pin ?? {});
    Object.assign(merged.bonus, entry.bonus ?? {});
    Object.assign(merged.note, entry.note ?? {});
    for (const id of entry.exclude ?? []) merged.exclude.add(String(id));
  }

  return merged;
}
