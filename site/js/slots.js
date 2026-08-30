// The paperdoll: which slots exist, which inventory types each accepts, and how they are laid out
// around the character panel.
//
// This is separate from config/slots.json's ranking model because the two answer different
// questions. The ranking model asks "how many of this do you wear" (two rings, one head); the
// paperdoll needs each of those as its own addressable slot so you can equip a different item in
// finger 1 and finger 2.

const MAIN_HAND = [13, 21];
const TWO_HAND = [17];
const OFF_HAND_WEAPON = [13, 22];
const SHIELD = [14];
const HELD_IN_OFF_HAND = [23];
const RANGED = [15, 25, 26, 28];

/** Left column, top to bottom, matching where the gear sits on a character. */
export const LEFT_SLOTS = [
  { key: 'head', name: 'Head', invTypes: [1] },
  { key: 'neck', name: 'Neck', invTypes: [2] },
  { key: 'shoulder', name: 'Shoulders', invTypes: [3] },
  { key: 'back', name: 'Back', invTypes: [16] },
  { key: 'chest', name: 'Chest', invTypes: [5, 20] },
  { key: 'wrist', name: 'Wrists', invTypes: [9] },
  { key: 'mainhand', name: 'Main Hand', invTypes: [...MAIN_HAND, ...TWO_HAND], weapon: 'main' },
  { key: 'offhand', name: 'Off Hand', invTypes: [...OFF_HAND_WEAPON, ...SHIELD, ...HELD_IN_OFF_HAND], weapon: 'off' }
];

export const RIGHT_SLOTS = [
  { key: 'hands', name: 'Hands', invTypes: [10] },
  { key: 'waist', name: 'Waist', invTypes: [6] },
  { key: 'legs', name: 'Legs', invTypes: [7] },
  { key: 'feet', name: 'Feet', invTypes: [8] },
  { key: 'finger1', name: 'Finger 1', invTypes: [11] },
  { key: 'finger2', name: 'Finger 2', invTypes: [11] },
  { key: 'trinket1', name: 'Trinket 1', invTypes: [12] },
  { key: 'trinket2', name: 'Trinket 2', invTypes: [12] },
  { key: 'ranged', name: 'Ranged / Relic', invTypes: RANGED, ranged: true }
];

export const ALL_SLOTS = [...LEFT_SLOTS, ...RIGHT_SLOTS];

export function slotByKey(key) {
  return ALL_SLOTS.find(s => s.key === key) ?? null;
}

/** Slots that hold the same kind of item, so an equip can move rather than duplicate. */
export function siblingSlots(key) {
  if (key.startsWith('finger')) return ['finger1', 'finger2'];
  if (key.startsWith('trinket')) return ['trinket1', 'trinket2'];
  return [key];
}

/** Scoring context: weapon damage only counts in a hand, and only a ranged weapon shoots. */
export function contextFor(slot) {
  if (slot.ranged) return 'ranged';
  // An off-hand swing deals half damage in 1.12, so its weapon dps is worth half a main hand's.
  // Scoring both hands at full value overrated every off-hand weapon for warriors and rogues.
  if (slot.weapon === 'off') return 'offhand';
  if (slot.weapon) return 'melee';
  return 'armor';
}

export function isTwoHanded(item) {
  return item?.slot === 17;
}
