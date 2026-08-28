// The paperdoll view: equipment slots down both sides, character summary in the middle.

import { esc, statLine, sourceLine } from './render.js';
import { iconUrl } from './data.js';
import { LEFT_SLOTS, RIGHT_SLOTS, isTwoHanded } from './slots.js';
import { summarise } from './equipped.js';
import { ENCHANTABLE, find as findEnchant } from './enchants.js';

const QUALITY_CLASS = ['poor', 'common', 'uncommon', 'rare', 'epic', 'legendary'];

/** The enchant line under a slot. Only shown once something is equipped there to enchant. */
function enchantLine(data, slot, item, enchants) {
  if (!ENCHANTABLE.has(slot.key) || !item) return '';

  const applied = findEnchant(data.enchantIndex, slot.key, enchants[slot.key]);

  return `
    <button class="enchant-line${applied ? ' is-set' : ''}" type="button" data-enchant-slot="${slot.key}">
      <span class="enchant-tag">${applied?.kind === 'gem' ? 'Gem' : applied?.kind === 'buckle' ? 'Buckle' : 'Ench'}</span>
      <span class="enchant-name">${applied ? esc(applied.name) : 'None'}</span>
    </button>`;
}

function slotCell(data, slot, gear, side, enchants) {
  const itemId = gear[slot.key];
  const item = itemId ? data.byId.get(itemId) : null;

  const covered = slot.key === 'offhand' && isTwoHanded(data.byId.get(gear.mainhand));

  const icon = iconUrl(data, item)
    ? `<img class="slot-icon" src="${esc(iconUrl(data, item))}" alt="" loading="lazy" width="44" height="44">`
    : '<span class="slot-icon slot-icon-empty" aria-hidden="true"></span>';

  const body = covered
    ? '<span class="slot-name">Off Hand</span><span class="slot-item muted">Two-handed weapon equipped</span>'
    : item
      ? `<span class="slot-name">${esc(slot.name)}</span>
         <span class="slot-item q-${QUALITY_CLASS[item.quality] ?? 'common'}" data-item="${item.id}">${esc(item.name)}</span>
         <span class="slot-stats">${esc(statLine(item))}</span>`
      : `<span class="slot-name">${esc(slot.name)}</span><span class="slot-item muted">Empty</span>`;

  return `
    <div class="slot-group">
      <button class="slot ${side}${covered ? ' is-covered' : ''}${item ? ' is-filled' : ''}"
              type="button" data-slot="${slot.key}"${covered ? ' disabled' : ''}>
        ${icon}
        <span class="slot-body">${body}</span>
      </button>
      ${covered ? '' : enchantLine(data, slot, item, enchants)}
    </div>`;
}

function summaryPanel(data, character, cls, spec, phase, gear, overrides, enchantStats, talentStats) {
  const summary = summarise(data, gear, spec, overrides, enchantStats, talentStats);
  const filled = summary.equippedCount;
  const total = LEFT_SLOTS.length + RIGHT_SLOTS.length;

  return `
    <div class="panel">
      <div class="panel-head">
        <div>
          <h2>${esc(character.name)}</h2>
          <p class="panel-sub" style="--class-color:${esc(cls.color)}">
            <span class="class-tag">${esc(cls.name)}</span> · ${esc(spec.name)} · ${esc(phase.name)}
          </p>
        </div>
        <div class="panel-score">
          <span class="score-value">${summary.score}</span>
          <span class="score-label">gear score</span>
        </div>
      </div>

      <div class="panel-actions">
        <button class="button" type="button" id="unequip-all">Unequip all</button>
        <button class="button button-primary" type="button" id="auto-fill">Auto-fill best in slot</button>
        <span class="fill-count">${filled} / ${total} slots</span>
      </div>

      ${filled === 0
        ? '<p class="empty">Nothing equipped yet. Click a slot to browse its ranked options, or auto-fill the whole set.</p>'
        : ''}

      ${summary.groups.length === 0 ? '' : `<div class="stat-groups">
            ${summary.groups.map(group => `
              <section class="stat-group">
                <h3>${esc(group.name)}</h3>
                <dl>
                  ${group.entries.map(entry => `
                    <div>
                      <dt>${esc(entry.label)}</dt>
                      <dd>${entry.value}${entry.breakdown
                        ? `<span class="stat-breakdown">${esc(entry.breakdown)}</span>` : ''}</dd>
                    </div>`).join('')}
                </dl>
              </section>`).join('')}
           </div>`}

      <p class="panel-note">
        Totals cover gear, enchants and any talent that grants a stat outright — Shadow Focus and
        the like are part of your hit, so they are counted here. Base class and race values are not
        in the dataset, so a combined health or mana figure would be mostly invention.
      </p>
    </div>`;
}

export function renderPlanner(data, character, cls, spec, phase, gear, overrides, enchants = {}, enchantStats = {}, talentStats = {}) {
  return `
    <div class="planner">
      <div class="slot-column">${LEFT_SLOTS.map(s => slotCell(data, s, gear, 'left', enchants)).join('')}</div>
      <div class="planner-centre">${summaryPanel(data, character, cls, spec, phase, gear, overrides, enchantStats, talentStats)}</div>
      <div class="slot-column">${RIGHT_SLOTS.map(s => slotCell(data, s, gear, 'right', enchants)).join('')}</div>
    </div>`;
}

/** Shown when there is no character yet. */
export function renderWelcome(data) {
  return `
    <section class="welcome">
      <h1>Best in slot for <span class="accent">OctoWoW</span></h1>
      <p class="lede">
        Build a character, pick a phase, then click any slot to see every item that fits it —
        ranked best to worst, with the boss, vendor or recipe that hands it over.
      </p>
      <button class="button button-primary button-large" type="button" id="create-first">Create a character</button>
      <p class="muted welcome-meta">${data.meta.counts.items} items indexed across ${data.phases.length} phases.</p>
    </section>`;
}

export function renderPickerRow(data, entry) {
  const { item } = entry;
  const source = entry.sources[0] ?? null;
  const icon = iconUrl(data, item)
    ? `<img class="row-icon" src="${esc(iconUrl(data, item))}" alt="" loading="lazy" width="32" height="32">`
    : '<span class="row-icon row-icon-empty" aria-hidden="true"></span>';

  return `
    <tr data-equip="${item.id}">
      <td>
        <div class="row-item">
          ${icon}
          <div>
            <span class="item-name q-${QUALITY_CLASS[item.quality] ?? 'common'}" data-item="${item.id}">${esc(item.name)}</span>
            ${entry.adjusted ? '<span class="badge badge-adjusted">adjusted</span>' : ''}
            ${item.setName ? `<span class="set-name">${esc(item.setName)}</span>` : ''}
            <div class="stat-line">${esc(statLine(item))}</div>
          </div>
        </div>
      </td>
      <td class="col-score"><span class="rating">${entry.rating}</span></td>
      <td class="source-cell">${sourceLine(source)}</td>
    </tr>`;
}
