// Item tooltips rendered locally from items.json - no external tooltip script, so the page stays
// self-contained and works even if the database is unreachable.

import { esc, statLine } from './render.js';

const QUALITY_CLASS = ['poor', 'common', 'uncommon', 'rare', 'epic', 'legendary'];

export function attachTooltips(root, data) {
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
    tip.innerHTML = build(item);
    tip.hidden = false;
    position(tip, target);
  });

  root.addEventListener('mouseout', event => {
    const target = event.target.closest('[data-item]');
    if (target && !target.contains(event.relatedTarget)) hide();
  });

  window.addEventListener('scroll', hide, { passive: true });
}

function build(item) {
  const rows = [];
  if (item.req) rows.push(`Requires level ${item.req}`);
  if (item.ilvl) rows.push(`Item level ${item.ilvl}`);
  if (item.subName) rows.push(esc(item.subName));
  if (item.classes.length) rows.push(`Classes: ${item.classes.map(c => c[0].toUpperCase() + c.slice(1)).join(', ')}`);

  return `
    <div class="tip-name q-${QUALITY_CLASS[item.quality] ?? 'common'}">${esc(item.name)}</div>
    ${item.setName ? `<div class="tip-set">${esc(item.setName)}</div>` : ''}
    <div class="tip-stats">${esc(statLine(item))}</div>
    <div class="tip-meta">${rows.join(' · ')}</div>
    ${item.notes?.length ? `<div class="tip-notes">${item.notes.map(n => esc(n)).join('<br>')}</div>` : ''}`;
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
