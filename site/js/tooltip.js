// Item tooltips rendered locally from items.json - no external tooltip script, so the page stays
// self-contained and works even if the database is unreachable.
//
// The layout follows the game's tooltip order, because that is the order players read: name,
// binding, slot and type, weapon damage, armour, stats, durability, restrictions, then the green
// effect lines, then the set block. Stats are printed as full sentences ("+22 Intellect") rather
// than the abbreviated stat line the lists use - a tooltip is where you go for the detail.

import { esc } from './render.js';
import { sourcesFor } from './data.js';

const QUALITY_CLASS = ['poor', 'common', 'uncommon', 'rare', 'epic', 'legendary'];

/** Inventory type -> the wording the game puts on the left of the tooltip's slot row. */
const SLOT_NAMES = {
  1: 'Head', 2: 'Neck', 3: 'Shoulder', 4: 'Shirt', 5: 'Chest', 6: 'Waist', 7: 'Legs',
  8: 'Feet', 9: 'Wrist', 10: 'Hands', 11: 'Finger', 12: 'Trinket', 13: 'One-Hand',
  14: 'Off Hand', 15: 'Ranged', 16: 'Back', 17: 'Two-Hand', 19: 'Tabard', 20: 'Chest',
  21: 'Main Hand', 22: 'Off Hand', 23: 'Held In Off-hand', 25: 'Thrown', 26: 'Ranged',
  28: 'Relic'
};

const BINDING_TEXT = {
  pickup: 'Binds when picked up',
  equip: 'Binds when equipped',
  use: 'Binds when used',
  quest: 'Quest Item'
};

/** Primary stats, in the order the game lists them. */
const PRIMARY = [
  ['str', 'Strength'], ['agi', 'Agility'], ['sta', 'Stamina'], ['int', 'Intellect'], ['spi', 'Spirit']
];

const SECONDARY = [
  ['defense', 'Defense'], ['dodge', 'Dodge'], ['parry', 'Parry'], ['block', 'Block']
];

const RESISTANCES = [
  ['resArcane', 'Arcane'], ['resFire', 'Fire'], ['resNature', 'Nature'],
  ['resFrost', 'Frost'], ['resShadow', 'Shadow']
];

export function attachTooltips(root, data, equippedIds = () => new Set()) {
  const tip = document.getElementById('tooltip');
  if (!tip) return;

  let current = null;

  const hide = () => {
    tip.hidden = true;
    current = null;
  };

  root.addEventListener('mouseover', event => {
    const target = event.target.closest('[data-item]');
    if (!target) return;

    const item = data.byId.get(Number(target.dataset.item));
    if (!item || current === item.id) return;

    current = item.id;
    tip.innerHTML = build(data, item, equippedIds());
    tip.hidden = false;
    position(tip, target);
  });

  root.addEventListener('mouseout', event => {
    const target = event.target.closest('[data-item]');
    if (target && !target.contains(event.relatedTarget)) hide();
  });

  window.addEventListener('scroll', hide, { passive: true });
}

function build(data, item, worn) {
  const out = [];
  const stats = item.stats ?? {};

  out.push(`<div class="tip-name q-${QUALITY_CLASS[item.quality] ?? 'common'}">${esc(item.name)}</div>`);

  if (item.unique) out.push(line('Unique'));
  if (item.bind && BINDING_TEXT[item.bind]) out.push(line(esc(BINDING_TEXT[item.bind])));

  const slotName = SLOT_NAMES[item.slot];
  const typeName = item.subName ? capitalise(item.subName) : '';
  if (slotName || typeName) out.push(split(esc(slotName ?? ''), esc(typeName)));

  // Weapons: damage range and speed share a row, with dps on its own line beneath.
  if (stats.weaponMinDmg && stats.weaponMaxDmg) {
    out.push(split(
      `${trim(stats.weaponMinDmg)} - ${trim(stats.weaponMaxDmg)} Damage`,
      stats.weaponSpeed ? `Speed ${stats.weaponSpeed.toFixed(2)}` : ''));
  }
  if (item.bonusDmg) out.push(line(esc(item.bonusDmg)));
  if (stats.weaponDps) out.push(line(`(${stats.weaponDps.toFixed(1)} damage per second)`));

  if (stats.armor) out.push(line(`${trim(stats.armor)} Armor`));

  for (const [key, label] of PRIMARY) if (stats[key]) out.push(line(`${signed(stats[key])} ${label}`));
  for (const [key, label] of SECONDARY) if (stats[key]) out.push(line(`${signed(stats[key])} ${label}`));
  for (const [key, label] of RESISTANCES) {
    if (stats[key]) out.push(line(`${signed(stats[key])} ${label} Resistance`));
  }
  if (stats.resAll) out.push(line(`${signed(stats.resAll)} All Resistances`));

  if (item.dur) out.push(line(`Durability ${item.dur} / ${item.dur}`));
  if (item.classes?.length) out.push(line(`Classes: ${item.classes.map(capitalise).map(esc).join(', ')}`));
  if (item.req) out.push(line(`Requires Level ${item.req}`));

  // Green effect lines, worded exactly as the database words them. Every stat that is not printed
  // above arrived through one of these, so the sentence is the only place it needs to appear -
  // "+20 spell damage" would just be the same fact in worse words.
  for (const effect of item.effects ?? []) {
    const prefix = effect.k === 'use' ? 'Use:' : effect.k === 'proc' ? 'Chance on hit:' : 'Equip:';
    out.push(`<div class="tip-effect">${esc(prefix)} ${esc(effect.t)}</div>`);
  }

  out.push(setBlock(data, item, worn));
  out.push(sourceBlock(data, item));

  return out.join('');
}

function line(text) {
  return `<div class="tip-line">${text}</div>`;
}

/** The two-column rows the game uses: slot on the left, item type on the right. */
function split(left, right) {
  return `<div class="tip-row"><span>${left}</span><span class="tip-right">${right ?? ''}</span></div>`;
}

function signed(value) {
  return value < 0 ? trim(value) : `+${trim(value)}`;
}

function trim(value) {
  return Number.isInteger(value) ? String(value) : String(Math.round(value * 100) / 100);
}

/**
 * The set block: the other pieces, and what wearing several of them gives.
 *
 * Pieces you already have equipped are white and counted, the rest grey, and a bonus turns green
 * once you are wearing enough of them. That is what the game does, and in a gear planner it answers
 * "how close am I to the 3-piece" without leaving the tooltip.
 */
function setBlock(data, item, worn) {
  if (item.setId === undefined || item.setId === null) return '';

  const set = data.sets?.[String(item.setId)];
  // The set name is on the item even where the block itself was never captured.
  if (!set) return item.setName ? `<div class="tip-set">${esc(item.setName)}</div>` : '';

  const equipped = set.pieces.filter(id => worn.has(id)).length;

  const pieces = set.pieces.map(id => {
    const piece = data.byId.get(id);
    return `<div class="tip-piece${worn.has(id) ? ' is-worn' : ''}">${esc(piece?.name ?? `Item ${id}`)}</div>`;
  }).join('');

  const bonuses = (set.bonuses ?? []).map(bonus =>
    `<div class="tip-bonus${equipped >= bonus.n ? ' is-active' : ''}">(${bonus.n}) Set: ${esc(bonus.t)}</div>`
  ).join('');

  return `
    <div class="tip-setblock">
      <div class="tip-set">${esc(set.name)} (${equipped}/${set.total || set.pieces.length})</div>
      ${pieces}${bonuses}
    </div>`;
}

/**
 * Where the item comes from. Not part of a game tooltip, but it is the question this whole site
 * exists to answer, so it belongs on the hover rather than one click away.
 */
function sourceBlock(data, item) {
  const sources = sourcesFor(data, item.id);
  if (sources.length === 0) return '';

  const first = sources[0];
  const bits = [];

  switch (first.kind) {
    case 'vendor': bits.push(`Sold by ${esc(first.name)}`); break;
    case 'quest': bits.push(`Quest: ${esc(first.name)}`); break;
    case 'craft': bits.push(`Crafted: ${esc(first.name || 'unknown recipe')}`); break;
    case 'reputation': bits.push(`Reputation: ${esc(first.name)}`); break;
    default: bits.push(esc(first.name));
  }

  if (first.zone) bits.push(esc(first.zone));
  if (first.percent) bits.push(`${first.percent.toFixed(1)}%`);

  const more = sources.length > 1 ? `<span class="tip-more"> · +${sources.length - 1} more</span>` : '';
  return `<div class="tip-source">${bits.join(' · ')}${more}</div>`;
}

function capitalise(text) {
  return String(text).charAt(0).toUpperCase() + String(text).slice(1);
}

function position(tip, target) {
  const rect = target.getBoundingClientRect();
  const width = tip.offsetWidth;
  const height = tip.offsetHeight;

  let left = rect.right + 12;
  if (left + width > window.innerWidth - 8) left = Math.max(8, rect.left - width - 12);

  let top = rect.top;
  if (top + height > window.innerHeight - 8) top = Math.max(8, window.innerHeight - height - 8);

  tip.style.left = `${left + window.scrollX}px`;
  tip.style.top = `${top + window.scrollY}px`;
}
