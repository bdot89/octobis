// Saved characters and their equipped gear, persisted to localStorage.
//
// Everything a visitor builds lives in their own browser - there is no account and no server. The
// gear set is keyed by phase as well as character, because the whole point of the site is that the
// answer changes per phase.

const STORAGE_KEY = 'octobis.characters.v1';
export const MAX_CHARACTERS = 8;

/** A character keeps a completely separate gear set and talent build per mode. */
export const MODES = [
  { id: 'pve', name: 'PvE', blurb: 'Raids and dungeons' },
  { id: 'pvp', name: 'PvP', blurb: 'Battlegrounds and world PvP' }
];

export function isMode(id) {
  return MODES.some(m => m.id === id);
}

function emptyLoadout() {
  return { gear: {}, talents: {} };
}

/**
 * Brings a stored character up to the current shape.
 *
 * Characters saved before loadouts existed kept gear and talents at the top level; that build is
 * their PvE set, and an empty PvP set is added alongside it.
 */
function migrate(character) {
  character.loadouts ??= {};

  if (character.gear || character.talents) {
    character.loadouts.pve ??= {
      gear: character.gear ?? {},
      talents: character.talents ?? {}
    };
    delete character.gear;
    delete character.talents;
  }

  for (const mode of MODES) character.loadouts[mode.id] ??= emptyLoadout();
  return character;
}

function load() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return { characters: [], activeId: null, mode: 'pve' };
    const parsed = JSON.parse(raw);
    return {
      characters: (Array.isArray(parsed.characters) ? parsed.characters : []).map(migrate),
      activeId: parsed.activeId ?? null,
      mode: isMode(parsed.mode) ? parsed.mode : 'pve'
    };
  } catch {
    // A corrupt or unreadable store must not take the page down with it.
    return { characters: [], activeId: null, mode: 'pve' };
  }
}

function save(state) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  } catch {
    // Private browsing and full quotas both land here; the session still works, it just won't persist.
  }
}

let state = load();

export function all() {
  return state.characters;
}

export function active() {
  return state.characters.find(c => c.id === state.activeId) ?? state.characters[0] ?? null;
}

export function setActive(id, mode) {
  state.activeId = id;
  if (isMode(mode)) state.mode = mode;
  save(state);
}

export function activeMode() {
  return state.mode ?? 'pve';
}

export function setMode(mode) {
  if (!isMode(mode)) return;
  state.mode = mode;
  save(state);
}

function loadout(character, mode = activeMode()) {
  if (!character) return emptyLoadout();
  character.loadouts ??= {};
  character.loadouts[mode] ??= emptyLoadout();
  return character.loadouts[mode];
}

export function isFull() {
  return state.characters.length >= MAX_CHARACTERS;
}

export function create({ name, gender, classId, specId, raceId }) {
  if (isFull()) throw new Error(`You can save up to ${MAX_CHARACTERS} characters.`);

  const character = {
    id: `c${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`,
    name: name.trim(),
    gender: gender === 'female' ? 'female' : 'male',
    classId,
    specId,
    raceId,
    // One loadout per mode; each holds gear per phase and a single talent build.
    loadouts: { pve: emptyLoadout(), pvp: emptyLoadout() }
  };

  state.characters.push(character);
  state.activeId = character.id;
  save(state);
  return character;
}

export function remove(id) {
  state.characters = state.characters.filter(c => c.id !== id);
  if (state.activeId === id) state.activeId = state.characters[0]?.id ?? null;
  save(state);
}

export function update(id, changes) {
  const character = state.characters.find(c => c.id === id);
  if (!character) return null;
  Object.assign(character, changes);
  save(state);
  return character;
}

// ---- Equipped gear ---------------------------------------------------------------------------

/** Slot keys are the display slot id plus an index for slots that take two (finger1, trinket2). */
export function gearFor(character, phaseId, mode) {
  if (!character) return {};
  return loadout(character, mode ?? activeMode()).gear[phaseId] ?? {};
}

export function equip(character, phaseId, slotKey, itemId) {
  if (!character) return;
  const gear = loadout(character).gear;
  gear[phaseId] ??= {};

  if (itemId === null) delete gear[phaseId][slotKey];
  else gear[phaseId][slotKey] = itemId;

  save(state);
}

export function unequipAll(character, phaseId) {
  if (!character) return;
  loadout(character).gear[phaseId] = {};
  save(state);
}

export function equipMany(character, phaseId, entries) {
  if (!character) return;
  loadout(character).gear[phaseId] = { ...entries };
  save(state);
}

/** Number of slots filled in a mode's set for a phase, for the character list. */
export function filledCount(character, phaseId, mode) {
  return Object.keys(gearFor(character, phaseId, mode)).length;
}

// ---- Talents ---------------------------------------------------------------------------------

/** Talents belong to the loadout, not the phase - a PvP spec is a different build, not new points. */
export function talentsFor(character, mode) {
  return loadout(character, mode ?? activeMode()).talents ?? {};
}

export function setTalents(character, build) {
  if (!character) return;
  loadout(character).talents = build;
  save(state);
}

export function talentPointsSpent(character, mode) {
  return Object.values(talentsFor(character, mode)).reduce((sum, ranks) => sum + ranks, 0);
}

// ---- Weapon skill ----------------------------------------------------------------------------

/**
 * Weapon skill belongs to the character rather than the loadout: the Fray Island quest is
 * permanent, so it applies whichever set you are looking at.
 */
export function weaponSkill(character, base = 300) {
  return character?.weaponSkill ?? base;
}

export function setWeaponSkill(character, value) {
  if (!character) return;
  character.weaponSkill = value;
  save(state);
}

// ---- Enchants --------------------------------------------------------------------------------

/** Enchants sit alongside gear: same loadout, same phase, keyed by slot. */
export function enchantsFor(character, phaseId, mode) {
  if (!character) return {};
  const set = loadout(character, mode ?? activeMode());
  return set.enchants?.[phaseId] ?? {};
}

export function setEnchant(character, phaseId, slotKey, enchantKey) {
  if (!character) return;
  const set = loadout(character);
  set.enchants ??= {};
  set.enchants[phaseId] ??= {};

  if (enchantKey === null) delete set.enchants[phaseId][slotKey];
  else set.enchants[phaseId][slotKey] = enchantKey;

  save(state);
}

/** Re-reads from storage, so another tab's changes are picked up on demand. */
export function refresh() {
  state = load();
}
