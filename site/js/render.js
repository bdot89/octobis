// Shared rendering helpers, plus the two flat list views. Everything here is pure: it takes data
// and returns HTML.

import { iconUrl } from './data.js';

const QUALITY_CLASS = ['poor', 'common', 'uncommon', 'rare', 'epic', 'legendary'];

/** Display order and labels for the compact stat line under an item name. */
const STAT_DISPLAY = [
  ['str', 'Str'], ['agi', 'Agi'], ['sta', 'Sta'], ['int', 'Int'], ['spi', 'Spi'],
  ['ap', 'AP'], ['rap', 'RAP'], ['spellPower', 'SP'], ['healPower', 'Heal'],
  ['crit', '% Crit'], ['hit', '% Hit'], ['spellCrit', '% Spell Crit'], ['spellHit', '% Spell Hit'],
  ['mp5', 'MP5'], ['mp5WhileCasting', '% Mana While Casting'], ['spellPen', 'Spell Pen'],
  ['haste', '% Attack Speed'], ['armorPen', 'Armor Pen'], ['leech', '% Leech'], ['hp5', 'HP5'],
  ['flatMeleeDamage', 'Wpn Dmg'],
  ['armor', 'Armor'], ['defense', 'Def'], ['dodge', '% Dodge'], ['parry', '% Parry'],
  ['block', '% Block'], ['blockValue', 'Block'],
  ['resFire', 'Fire Res'], ['resFrost', 'Frost Res'], ['resNature', 'Nature Res'],
  ['resShadow', 'Shadow Res'], ['resArcane', 'Arcane Res'],
  ['weaponDps', 'DPS'], ['weaponSpeed', 'Speed']
];

/** Readable names for stat keys, shared by everything that has to describe a stat in prose. */
export const STAT_LABELS = {
  str: 'Strength', agi: 'Agility', sta: 'Stamina', int: 'Intellect', spi: 'Spirit',
  ap: 'attack power', rap: 'ranged attack power', spellPower: 'spell damage',
  healPower: 'healing', crit: '% crit', hit: '% hit', spellCrit: '% spell crit',
  spellHit: '% spell hit', mp5: 'MP5', hp5: 'HP5', mp5WhileCasting: '% mana regen while casting',
  armor: 'armor', defense: 'defense', dodge: '% dodge', parry: '% parry',
  block: '% block', blockValue: 'block value', haste: '% attack speed',
  armorPen: 'armor penetration', leech: '% leech', spellPen: 'spell penetration',
  flatMeleeDamage: 'bonus weapon damage',
  weaponDps: 'weapon DPS', weaponSpeed: 'weapon speed',
  resFire: 'fire resistance', resFrost: 'frost resistance', resNature: 'nature resistance',
  resShadow: 'shadow resistance', resArcane: 'arcane resistance',
  spellDmgArcane: 'arcane damage', spellDmgFire: 'fire damage', spellDmgFrost: 'frost damage',
  spellDmgHoly: 'holy damage', spellDmgNature: 'nature damage', spellDmgShadow: 'shadow damage'
};

export function statLabel(key) {
  return STAT_LABELS[key] ?? key;
}

export function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, c =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function qualityClass(quality) {
  return QUALITY_CLASS[quality] ?? 'common';
}

export function statLine(item) {
  const parts = [];
  for (const [key, label] of STAT_DISPLAY) {
    const value = item.stats[key];
    if (!value) continue;

    // Penalties are real - Corrupted Ashbringer really does cost you stamina - so the sign has to
    // come from the value rather than always being a plus.
    const signed = value < 0 ? `${value}` : `+${value}`;

    if (key === 'weaponSpeed') parts.push(`${label} ${value.toFixed(2)}`);
    else if (label.startsWith('%')) parts.push(`${signed}${label}`);
    else parts.push(`${signed} ${label}`);
  }
  return parts.join(', ');
}

function money(copper) {
  if (!copper) return '';
  const gold = Math.floor(copper / 10000);
  const silver = Math.floor((copper % 10000) / 100);
  const bits = [];
  if (gold) bits.push(`${gold}g`);
  if (silver) bits.push(`${silver}s`);
  return bits.join(' ') || `${copper}c`;
}

/** One-line description of where an item comes from. */
export function sourceLine(source) {
  if (!source) return '<span class="src-unknown">Source unknown</span>';

  const zone = source.zone ? `<span class="src-zone">${esc(source.zone)}</span>` : '';
  const boss = source.boss ? '<span class="badge badge-boss">Boss</span>' : '';

  switch (source.kind) {
    case 'drop': {
      const pct = source.percent ? `<span class="src-pct">${source.percent.toFixed(1)}%</span>` : '';
      const token = source.token ? '<span class="badge badge-token" title="Exchanged from a token">token</span>' : '';
      return `${boss}${token}<span class="src-name">${esc(source.name)}</span>${zone ? ' · ' + zone : ''}${pct ? ' · ' + pct : ''}`;
    }
    case 'vendor': {
      const cost = source.cost ? ` · <span class="src-pct">${money(source.cost)}</span>` : '';
      return `<span class="badge badge-vendor">Vendor</span><span class="src-name">${esc(source.name)}</span>${zone ? ' · ' + zone : ''}${cost}`;
    }
    case 'quest':
      return `<span class="badge badge-quest">Quest</span><span class="src-name">${esc(source.name)}</span>${zone ? ' · ' + zone : ''}`;
    case 'craft':
      return `<span class="badge badge-craft">Crafted</span><span class="src-name">${esc(source.name || 'Crafted')}</span>`;
    case 'reputation':
      return `<span class="badge badge-rep">Reputation</span><span class="src-name">${esc(source.name)}</span>`;
    case 'object':
      return `<span class="badge badge-object">Container</span><span class="src-name">${esc(source.name)}</span>${zone ? ' · ' + zone : ''}`;
    default:
      return `<span class="src-name">${esc(source.name)}</span>`;
  }
}

/** Sources are pre-sorted by phase then drop chance, so the first is the most accessible. */
function primarySource(entry) {
  return entry.sources[0] ?? null;
}

function itemCell(data, entry, { rank }) {
  const { item } = entry;
  const icon = iconUrl(data, item);
  const url = `${data.itemUrlBase}${item.id}`;

  return `
    <div class="item-cell">
      ${icon ? `<img class="item-icon" src="${esc(icon)}" alt="" loading="lazy" width="36" height="36">`
             : '<span class="item-icon item-icon-missing" aria-hidden="true"></span>'}
      <div class="item-body">
        <a class="item-name q-${qualityClass(item.quality)}" href="${esc(url)}"
           target="_blank" rel="noopener noreferrer" data-item="${item.id}">${esc(item.name)}</a>
        ${rank ? `<span class="rank-badge">#${rank}</span>` : ''}
        ${entry.adjusted ? '<span class="badge badge-adjusted" title="Manually adjusted by an override">adjusted</span>' : ''}
        ${item.setName ? `<span class="set-name">${esc(item.setName)}</span>` : ''}
        <div class="stat-line">${esc(statLine(item))}</div>
        ${entry.note ? `<div class="item-note">${esc(entry.note)}</div>` : ''}
      </div>
    </div>`;
}

function slotRow(data, row) {
  if (!row.picks.length) {
    return `<tr class="row-pick row-empty">
      <th class="slot-name-cell" scope="row">${esc(row.name)}</th>
      <td colspan="2" class="empty-reason">${esc(row.emptyReason ?? 'No candidates.')}</td>
    </tr>`;
  }

  const equipped = row.equipped ?? 1;
  const winners = row.picks.slice(0, equipped);
  const alternatives = row.picks.slice(equipped);

  const winnerHtml = winners.map((entry, index) => `
    <tr class="row-pick">
      ${index === 0 ? `<th class="slot-name-cell" rowspan="${winners.length}" scope="row">${esc(row.name)}</th>` : ''}
      <td>${itemCell(data, entry, { rank: 0 })}</td>
      <td class="source-cell">${sourceLine(primarySource(entry))}</td>
    </tr>`).join('');

  if (!alternatives.length) return winnerHtml;

  const altHtml = alternatives.map((entry, index) => `
    <tr class="row-alt">
      <td>${itemCell(data, entry, { rank: index + 1 + equipped })}</td>
      <td class="source-cell">${sourceLine(primarySource(entry))}</td>
    </tr>`).join('');

  return `${winnerHtml}
    <tr class="row-toggle">
      <td colspan="3"><button class="alt-toggle" type="button" aria-expanded="false">
        Show ${alternatives.length} alternative${alternatives.length === 1 ? '' : 's'}
      </button></td>
    </tr>
    ${altHtml}`;
}

export function renderBisTable(data, rows) {
  if (!rows.length) {
    return '<p class="empty">No items scored above zero for this spec in this phase. Either the phase has no data yet, or this spec\'s stat weights need attention.</p>';
  }

  return `
    <table class="bis-table">
      <thead>
        <tr><th scope="col">Slot</th><th scope="col">Item</th><th scope="col">How to get it</th></tr>
      </thead>
      <tbody>${rows.map(row => slotRow(data, row)).join('')}</tbody>
    </table>`;
}

/**
 * The checklist regroups the same picks by where they come from, which is the view that answers
 * "what do I actually need to run this week".
 */
export function renderChecklist(data, rows) {
  const groups = new Map();

  for (const row of rows) {
    const equipped = row.equipped ?? 1;
    for (const entry of row.picks.slice(0, equipped)) {
      const source = primarySource(entry);
      const key = groupKey(source);
      if (!groups.has(key)) groups.set(key, { label: key, entries: [] });
      groups.get(key).entries.push({ entry, source, slot: row.name });
    }
  }

  if (!groups.size) return '<p class="empty">Nothing to collect — the BiS list is empty for this phase.</p>';

  const ordered = [...groups.values()].sort((a, b) => b.entries.length - a.entries.length);
  // Within an instance, list items in the order the encounters are pulled.
  for (const group of ordered) {
    group.entries.sort((a, b) => (a.source?.order ?? 99) - (b.source?.order ?? 99));
  }

  return `<div class="checklist">${ordered.map(group => `
    <section class="checklist-group">
      <h3>${esc(group.label)} <span class="count">${group.entries.length} item${group.entries.length === 1 ? '' : 's'}</span></h3>
      <ul>
        ${group.entries.map(({ entry, source, slot }) => `
          <li>
            <span class="check-slot">${esc(slot)}</span>
            <a class="item-name q-${qualityClass(entry.item.quality)}"
               href="${esc(data.itemUrlBase + entry.item.id)}" target="_blank" rel="noopener noreferrer">${esc(entry.item.name)}</a>
            <span class="check-source">${sourceLine(source)}</span>
          </li>`).join('')}
      </ul>
    </section>`).join('')}</div>`;
}

function groupKey(source) {
  if (!source) return 'Source unknown';
  if (source.kind === 'craft') return source.instance ? `Crafted — ${source.instance}` : 'Crafted';
  if (source.kind === 'reputation') return source.instance ? `Reputation — ${source.instance}` : 'Reputation';
  if (source.kind === 'quest') return 'Quest rewards';
  // The instance name is a better grouping than the raw zone: it separates the wings and custom
  // dungeons that share a zone id.
  return source.instance || source.zone || 'World drops';
}

