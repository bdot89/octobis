// Controller: loads data, owns the current character/phase/view, and wires the modals.

import { loadAll, overridesFor } from './data.js';
import { renderBisTable, renderChecklist, esc, statLabel } from './render.js';
import { renderPlanner, renderWelcome, renderPickerRow } from './planner.js';
import { buildBis } from './score.js';
import { candidatesFor, normalise, autoFill } from './equipped.js';
import { renderTalents, addPoint, removePoint, treesFor } from './talents.js';
import { renderGuide } from './guide.js';
import { hitProfile, talentHit, hitKindFor } from './hit.js';
import { forSlot as enchantsForSlot, appliedStats, find as findEnchant } from './enchants.js';
import { slotByKey, siblingSlots, isTwoHanded } from './slots.js';
import { attachTooltips } from './tooltip.js';
import * as Characters from './character.js';

const view = document.getElementById('view');
const stamp = document.getElementById('data-stamp');

let data = null;
let phaseId = 0;
let currentView = 'planner';

// Draft state for the character creation modal.
const draft = { gender: 'male', classId: null, specId: null, raceId: null };
let pickerSlot = null;

// ---- Context ---------------------------------------------------------------------------------

function classOf(character) {
  return data.classes.find(c => c.id === character?.classId) ?? null;
}

function specOf(character) {
  const cls = classOf(character);
  return cls?.specs.find(s => s.id === character.specId) ?? cls?.specs[0] ?? null;
}

function phaseOf() {
  return data.phases.find(p => p.id === phaseId) ?? data.phases[0];
}

function overridesOf(character) {
  return overridesFor(data, character.classId, character.specId, phaseId);
}

/**
 * Stats the talent build grants outright, for folding into the character totals.
 *
 * Only hit for now, because it is the only talent effect the config quantifies - and the only one
 * the planner would otherwise report wrongly. A Shadow priest with 5/5 Shadow Focus has 10% spell
 * hit before equipping anything, so a totals panel that counts gear alone tells them they are 10%
 * further from the cap than they really are, and the number it shows is not their character's.
 */
/**
 * How much hit each BiS row already has, taken from the auto-filled set.
 *
 * The BiS list ranks slot by slot, so it needs to know what the rest of the set supplies before it
 * can tell a useful point of hit from a wasted one. Auto-fill has already solved that, so the list
 * borrows its answer rather than solving it again differently.
 */
function hitBySlotFor(character, budget) {
  if (!budget) return {};

  const spec = specOf(character);
  const cls = classOf(character);
  const gear = autoFill(data, character, spec, cls, phaseId, overridesOf(character), budget);

  // Planner slot keys map onto BiS row ids; rings and trinkets are two slots under one row.
  const rows = {
    head: ['head'], neck: ['neck'], shoulder: ['shoulder'], back: ['back'], chest: ['chest'],
    wrist: ['wrist'], hands: ['hands'], waist: ['waist'], legs: ['legs'], feet: ['feet'],
    finger: ['finger1', 'finger2'], trinket: ['trinket1', 'trinket2'],
    weapon: ['mainhand'], offhand: ['offhand'], ranged: ['ranged']
  };

  const hit = {};
  for (const [rowId, keys] of Object.entries(rows)) {
    hit[rowId] = keys.reduce(
      (sum, key) => sum + (data.byId.get(gear[key])?.stats?.[budget.statKey] ?? 0), 0);
  }
  return hit;
}

/** Ids of everything the active character is wearing in the current phase and loadout. */
function equippedIds() {
  const character = Characters.active();
  if (!character) return new Set();
  return new Set(Object.values(Characters.gearFor(character, phaseId)));
}

function talentStatsFor(character, spec) {
  const contributed = talentHit(
    data.hitcaps, data.talents, character.classId, spec, Characters.talentsFor(character));

  if (!contributed.total) return {};
  return { [hitKindFor(spec) === 'spell' ? 'spellHit' : 'hit']: contributed.total };
}

// ---- Rendering -------------------------------------------------------------------------------

function render() {
  const character = Characters.active();

  renderTopbar(character);

  if (!character) {
    view.innerHTML = renderWelcome(data);
    return;
  }

  const cls = classOf(character);
  const spec = specOf(character);
  const overrides = overridesOf(character);

  if (currentView === 'planner') {
    const enchants = Characters.enchantsFor(character, phaseId);
    view.innerHTML = renderPlanner(
      data, character, cls, spec, phaseOf(), Characters.gearFor(character, phaseId), overrides,
      enchants, appliedStats(data.enchantIndex, enchants), talentStatsFor(character, spec));
    return;
  }

  if (currentView === 'guide') {
    view.innerHTML = renderGuide(
      data, data.hitcaps, character, cls, spec, phaseOf(),
      Characters.gearFor(character, phaseId), Characters.talentsFor(character),
      Characters.activeMode(),
      {
        overrides,
        weaponSkill: Characters.weaponSkill(character, data.hitcaps.weaponSkill?.base ?? 300),
        enchants: Characters.enchantsFor(character, phaseId)
      });
    return;
  }

  if (currentView === 'talents') {
    view.innerHTML = renderTalents(
      data.talents, character.classId, Characters.talentsFor(character), spec);
    return;
  }

  const budget = hitBudgetFor(character);
  const rows = buildBis(data, cls, spec, phaseId, overrides, budget, hitBySlotFor(character, budget));
  view.innerHTML = `
    <div class="list-view">
      <h1>${esc(cls.name)} <span class="spec-name">${esc(spec.name)}</span>
        <span class="muted">· ${esc(phaseOf().name)}</span></h1>
      ${currentView === 'bis' ? renderBisTable(data, rows) : renderChecklist(data, rows)}
    </div>`;
}

function renderTopbar(character) {
  const label = document.getElementById('character-current');
  const cls = classOf(character);
  label.innerHTML = character
    ? `<span style="color:${esc(cls?.color ?? '#fff')}">${esc(character.name)}</span>
       <span class="topbar-sub">${esc(specOf(character)?.name ?? '')}</span>`
    : 'No character';

  document.getElementById('phase-current').textContent = phaseOf().name;

  const mode = Characters.activeMode();
  for (const button of document.querySelectorAll('#mode-toggle [data-mode]')) {
    button.classList.toggle('is-active', button.dataset.mode === mode);
  }
}

// ---- Dropdowns -------------------------------------------------------------------------------

function closeDropdowns() {
  for (const menu of document.querySelectorAll('.dropdown')) menu.hidden = true;
  for (const button of document.querySelectorAll('.topbar-button')) button.setAttribute('aria-expanded', 'false');
}

function toggleDropdown(button, menu, contents) {
  const wasOpen = !menu.hidden;
  closeDropdowns();
  if (wasOpen) return;

  menu.innerHTML = contents();
  menu.hidden = false;
  button.setAttribute('aria-expanded', 'true');
}

function characterMenu() {
  const characters = Characters.all();
  const activeId = Characters.active()?.id;
  const activeMode = Characters.activeMode();

  // Every character offers both loadouts, so picking one selects the character and the mode at once.
  const rows = characters.map(c => {
    const cls = data.classes.find(x => x.id === c.classId);
    const modes = Characters.MODES.map(mode => {
      const filled = Characters.filledCount(c, phaseId, mode.id);
      const points = Characters.talentPointsSpent(c, mode.id);
      const isActive = c.id === activeId && mode.id === activeMode;

      return `
        <button class="loadout-item${isActive ? ' is-active' : ''}" type="button"
                data-pick-character="${c.id}" data-pick-mode="${mode.id}">
          <span class="loadout-name">${esc(mode.name)}</span>
          <span class="muted">${filled} gear · ${points} talents</span>
        </button>`;
    }).join('');

    return `
      <div class="character-entry">
        <div class="character-head">
          <span style="color:${esc(cls?.color ?? '#fff')}">${esc(c.name)}</span>
          <span class="muted">${esc(cls?.name ?? '')} · ${esc(c.specId)}</span>
          <button class="icon-button small" type="button" data-delete-character="${c.id}"
                  aria-label="Delete ${esc(c.name)}">✕</button>
        </div>
        <div class="loadout-row">${modes}</div>
      </div>`;
  }).join('');

  const full = Characters.isFull();
  return `${rows || '<p class="dropdown-empty">No characters yet.</p>'}
    <button class="dropdown-item dropdown-action" type="button" id="open-create" ${full ? 'disabled' : ''}>
      ${full ? `Limit of ${Characters.MAX_CHARACTERS} reached` : '+ Create a character'}
    </button>`;
}

function phaseMenu() {
  return data.phases.map(p => `
    <button class="dropdown-item" type="button" data-pick-phase="${p.id}">
      <span>${esc(p.name)}${p.headline ? `: ${esc(p.headline)}` : ''}</span>
      <span class="muted">${p.status === 'live' ? 'Live' : esc(p.date ?? 'TBA')}</span>
      ${p.id === phaseId ? '<span class="tick">✓</span>' : ''}
    </button>`).join('');
}

// ---- Character creation ----------------------------------------------------------------------

function openCreateModal() {
  draft.classId = null;
  draft.specId = null;
  draft.raceId = null;
  draft.gender = 'male';

  document.getElementById('new-name').value = '';
  document.getElementById('character-error').hidden = true;
  for (const b of document.querySelectorAll('.gender-button')) {
    b.classList.toggle('is-active', b.dataset.gender === 'male');
  }

  renderClassGrid();
  renderSpecGrid();
  renderRaceGrid();
  updateCreateButton();

  document.getElementById('character-modal').hidden = false;
  document.getElementById('new-name').focus();
}

function renderClassGrid() {
  document.getElementById('class-grid').innerHTML = data.classes.map(c => `
    <button class="pick${draft.classId === c.id ? ' is-active' : ''}" type="button"
            data-pick-class="${c.id}" style="--class-color:${esc(c.color)}">
      <span class="pick-name">${esc(c.name)}</span>
    </button>`).join('');
}

function renderSpecGrid() {
  const grid = document.getElementById('spec-grid');
  const cls = data.classes.find(c => c.id === draft.classId);

  grid.innerHTML = cls
    ? cls.specs.map(s => `
        <button class="pick${draft.specId === s.id ? ' is-active' : ''}" type="button" data-pick-spec="${s.id}">
          <span class="pick-name">${esc(s.name)}</span>
          <span class="pick-sub">${esc(roleLabel(s.role))}</span>
        </button>`).join('')
    : '<p class="hint">Select a class first.</p>';
}

function renderRaceGrid() {
  document.getElementById('race-grid').innerHTML = data.races.map(r => {
    // A race is only offered if it can be the chosen class.
    const allowed = !draft.classId || r.classes.includes(draft.classId);
    return `
      <button class="pick race${draft.raceId === r.id ? ' is-active' : ''}${allowed ? '' : ' is-disabled'}"
              type="button" data-pick-race="${r.id}" ${allowed ? '' : 'disabled'}>
        <span class="pick-name">${esc(r.name)}</span>
        <span class="pick-sub faction-${esc(r.faction)}">${esc(data.factions[r.faction]?.name ?? '')}</span>
      </button>`;
  }).join('');
}

function updateCreateButton() {
  const name = document.getElementById('new-name').value.trim();
  document.getElementById('create-character').disabled =
    !(name.length > 0 && draft.classId && draft.specId && draft.raceId);
}

function roleLabel(role) {
  return { dps: 'Damage', healer: 'Healer', tank: 'Tank' }[role] ?? role;
}

// ---- Slot picker -----------------------------------------------------------------------------

function openPicker(slotKey) {
  const character = Characters.active();
  if (!character) return;

  pickerSlot = slotKey;
  const slot = slotByKey(slotKey);
  document.getElementById('picker-title').textContent = `Select ${slot?.name ?? 'Item'}`;
  document.getElementById('picker-search').value = '';
  document.getElementById('picker-quality').value = '0';
  document.getElementById('picker-source').value = '';

  renderPickerRows();
  document.getElementById('picker-modal').hidden = false;
  document.getElementById('picker-search').focus();
}

/** Items with no stats at all cannot be scored: relics, and trinkets that are pure proc. */
function hasScoreableStats(item) {
  return Object.keys(item.stats ?? {}).length > 0;
}

function pickerCandidates() {
  const character = Characters.active();
  return normalise(candidatesFor(
    data, character, specOf(character), classOf(character), phaseId, pickerSlot, overridesOf(character)));
}

function renderPickerRows() {
  const search = document.getElementById('picker-search').value.trim().toLowerCase();
  const minQuality = Number(document.getElementById('picker-quality').value);
  const sourceFilter = document.getElementById('picker-source').value;
  const usableOnly = document.getElementById('picker-usable').checked;

  const all = pickerCandidates();

  // The source filter is rebuilt from whatever this slot actually offers.
  const kinds = [...new Set(all.flatMap(c => c.sources.map(s => s.kind)))].sort();
  const select = document.getElementById('picker-source');
  if (select.dataset.slot !== pickerSlot) {
    select.dataset.slot = pickerSlot;
    select.innerHTML = '<option value="">All sources</option>' +
      kinds.map(k => `<option value="${esc(k)}">${esc(k[0].toUpperCase() + k.slice(1))}</option>`).join('');
  }

  // "Scores nothing for this spec" and "has nothing to score" are different things, and the filter
  // used to treat them alike. Librams, totems and idols carry no stats at all - they modify
  // abilities - so every relic scored zero and the Ranged / Relic slot came up empty for Paladin,
  // Shaman and Druid. An item with no stats is never hidden; there is nothing to judge it on.
  const filtered = all.filter(c =>
    (!search || c.item.name.toLowerCase().includes(search)) &&
    c.item.quality >= minQuality &&
    (!sourceFilter || c.sources.some(s => s.kind === sourceFilter)) &&
    (!usableOnly || c.score > 0 || !hasScoreableStats(c.item)));

  document.getElementById('picker-rows').innerHTML =
    filtered.slice(0, 300).map(entry => renderPickerRow(data, entry)).join('');
  document.getElementById('picker-empty').hidden = filtered.length > 0;

  // Where nothing in the slot can be scored, say so rather than showing a column of zeroes.
  const unscorable = filtered.length > 0 && filtered.every(c => !hasScoreableStats(c.item));
  const note = document.getElementById('picker-note');
  note.hidden = !unscorable;
  if (unscorable) {
    note.textContent = 'These carry no stats — they change how an ability behaves, which the '
      + 'stat-weight engine cannot rank. They are listed in database order; pick the one that suits '
      + 'your spec.';
  }
}

function equipItem(itemId) {
  const character = Characters.active();
  const item = data.byId.get(itemId);
  if (!character || !item) return;

  const gear = { ...Characters.gearFor(character, phaseId) };

  // A unique item cannot occupy both rings or both trinkets at once - but an ordinary one can,
  // and people do wear two of the same ring. Only clear the sibling when the game would.
  if (item.unique) {
    for (const sibling of siblingSlots(pickerSlot)) {
      if (sibling !== pickerSlot && gear[sibling] === itemId) delete gear[sibling];
    }
  }

  gear[pickerSlot] = itemId;

  // Equipping a two-hander clears the off hand, and filling the off hand clears a two-hander.
  if (pickerSlot === 'mainhand' && isTwoHanded(item)) delete gear.offhand;
  if (pickerSlot === 'offhand' && isTwoHanded(data.byId.get(gear.mainhand))) delete gear.mainhand;

  Characters.equipMany(character, phaseId, gear);
  dropStrandedEnchants(character, gear);
  closeModals();
  render();
}

/**
 * Clears any enchant whose slot no longer offers it.
 *
 * Enchants are stored per slot, not per item, so swapping a shield out for a sword left the
 * shield's Greater Stamina still counted and still displayed. Re-checking against what the slot
 * actually offers now is the same test the enchant picker uses.
 */
function dropStrandedEnchants(character, gear) {
  const applied = Characters.enchantsFor(character, phaseId);

  for (const [slotKey, key] of Object.entries(applied)) {
    if (!key) continue;

    const item = data.byId.get(gear[slotKey]);
    const offered = item
      ? enchantsForSlot(data.enchantIndex, slotKey, item)
      : [];

    if (!offered.some(e => e.key === key)) Characters.setEnchant(character, phaseId, slotKey, null);
  }
}

function closeModals() {
  for (const modal of document.querySelectorAll('.modal-backdrop')) modal.hidden = true;
}

// ---- Enchant picker --------------------------------------------------------------------------

let enchantSlot = null;

function openEnchantPicker(slotKey) {
  enchantSlot = slotKey;
  document.getElementById('enchant-title').textContent = `Enchant — ${slotByKey(slotKey)?.name ?? slotKey}`;
  document.getElementById('enchant-search').value = '';
  renderEnchantRows();
  document.getElementById('enchant-modal').hidden = false;
  document.getElementById('enchant-search').focus();
}

function renderEnchantRows() {
  const character = Characters.active();
  const gear = Characters.gearFor(character, phaseId);
  const equipped = data.byId.get(gear[enchantSlot]);
  const applied = Characters.enchantsFor(character, phaseId)[enchantSlot];

  const search = document.getElementById('enchant-search').value.trim().toLowerCase();
  const all = enchantsForSlot(data.enchantIndex, enchantSlot, equipped);
  const filtered = all.filter(e => !search || e.name.toLowerCase().includes(search));

  document.getElementById('enchant-rows').innerHTML = filtered.map(e => `
    <li>
      <button class="enchant-option${e.key === applied ? ' is-active' : ''}" type="button" data-enchant="${esc(e.key)}">
        <span class="enchant-option-name">${esc(e.name)}</span>
        <span class="enchant-option-stats">${esc(describeEnchant(e))}</span>
        <span class="enchant-option-source">${esc(e.source)}</span>
      </button>
    </li>`).join('');

  document.getElementById('enchant-empty').hidden = filtered.length > 0;
}

/** What an enchant gives, or an honest note when its value is not modelled. */
function describeEnchant(enchant) {
  const stats = Object.entries(enchant.stats);
  if (stats.length > 0) {
    return stats.map(([key, value]) => {
      const name = statLabel(key);
      return name.startsWith('% ') ? `+${value}%${name.slice(1)}` : `+${value} ${name}`;
    }).join(', ');
  }
  if (enchant.note) return enchant.note;
  if (enchant.proc) return `Proc — ${enchant.effect ?? 'conditional effect'} (not counted in totals)`;
  return enchant.effect ?? 'Effect not recorded — applied, but not counted in totals';
}

// ---- Events ----------------------------------------------------------------------------------

function wire() {
  document.getElementById('character-button').addEventListener('click', e => {
    toggleDropdown(e.currentTarget, document.getElementById('character-menu'), characterMenu);
  });

  document.getElementById('phase-button').addEventListener('click', e => {
    toggleDropdown(e.currentTarget, document.getElementById('phase-menu'), phaseMenu);
  });

  document.addEventListener('click', event => {
    if (!event.target.closest('.topbar-select')) closeDropdowns();
  });

  // Top bar menus.
  document.getElementById('character-menu').addEventListener('click', event => {
    const pick = event.target.closest('[data-pick-character]');
    if (pick) {
      Characters.setActive(pick.dataset.pickCharacter, pick.dataset.pickMode);
      closeDropdowns();
      render();
      return;
    }

    const del = event.target.closest('[data-delete-character]');
    if (del) {
      Characters.remove(del.dataset.deleteCharacter);
      document.getElementById('character-menu').innerHTML = characterMenu();
      render();
      return;
    }

    if (event.target.closest('#open-create')) { closeDropdowns(); openCreateModal(); }
  });

  document.getElementById('phase-menu').addEventListener('click', event => {
    const pick = event.target.closest('[data-pick-phase]');
    if (!pick) return;
    phaseId = Number(pick.dataset.pickPhase);
    closeDropdowns();
    render();
  });

  document.getElementById('mode-toggle').addEventListener('click', event => {
    const button = event.target.closest('[data-mode]');
    if (!button) return;
    Characters.setMode(button.dataset.mode);
    render();
  });

  for (const link of document.querySelectorAll('.nav-link[data-view]')) {
    link.addEventListener('click', () => {
      currentView = link.dataset.view;
      for (const other of document.querySelectorAll('.nav-link[data-view]')) {
        other.classList.toggle('is-active', other === link);
      }
      render();
    });
  }

  // Main view: slot clicks and panel actions.
  view.addEventListener('click', event => {
    if (event.target.closest('#create-first')) { openCreateModal(); return; }

    const enchantSlot = event.target.closest('[data-enchant-slot]');
    if (enchantSlot) { openEnchantPicker(enchantSlot.dataset.enchantSlot); return; }

    const slot = event.target.closest('[data-slot]');
    if (slot && !slot.disabled) { openPicker(slot.dataset.slot); return; }

    const character = Characters.active();
    if (event.target.closest('#unequip-all')) {
      Characters.unequipAll(character, phaseId);
      render();
      return;
    }

    if (event.target.closest('#auto-fill')) {
      const equipped = autoFill(
        data, character, specOf(character), classOf(character), phaseId, overridesOf(character),
        hitBudgetFor(character));
      Characters.equipMany(character, phaseId, equipped);
      render();
      return;
    }

    const weaponSkill = event.target.closest('[data-weapon-skill]');
    if (weaponSkill) {
      Characters.setWeaponSkill(character, Number(weaponSkill.dataset.weaponSkill));
      render();
      return;
    }

    if (event.target.closest('#reset-talents')) {
      Characters.setTalents(character, {});
      render();
      return;
    }

    const talent = event.target.closest('.talent');
    if (talent) { adjustTalent(talent, +1); return; }

    const toggle = event.target.closest('.alt-toggle');
    if (toggle) toggleAlternatives(toggle);
  });

  // Right-click removes a point, the way every talent calculator behaves.
  view.addEventListener('contextmenu', event => {
    const talent = event.target.closest('.talent');
    if (!talent) return;
    event.preventDefault();
    adjustTalent(talent, -1);
  });

  // Character modal.
  const modal = document.getElementById('character-modal');
  modal.addEventListener('click', event => {
    if (event.target === modal || event.target.closest('[data-close-modal]')) { closeModals(); return; }

    const gender = event.target.closest('[data-gender]');
    if (gender) {
      draft.gender = gender.dataset.gender;
      for (const b of document.querySelectorAll('.gender-button')) b.classList.toggle('is-active', b === gender);
      return;
    }

    const cls = event.target.closest('[data-pick-class]');
    if (cls) {
      draft.classId = cls.dataset.pickClass;
      draft.specId = null;
      // Changing class can invalidate the chosen race.
      const race = data.races.find(r => r.id === draft.raceId);
      if (race && !race.classes.includes(draft.classId)) draft.raceId = null;
      renderClassGrid(); renderSpecGrid(); renderRaceGrid(); updateCreateButton();
      return;
    }

    const spec = event.target.closest('[data-pick-spec]');
    if (spec) { draft.specId = spec.dataset.pickSpec; renderSpecGrid(); updateCreateButton(); return; }

    const race = event.target.closest('[data-pick-race]');
    if (race) { draft.raceId = race.dataset.pickRace; renderRaceGrid(); updateCreateButton(); }
  });

  document.getElementById('new-name').addEventListener('input', updateCreateButton);

  document.getElementById('create-character').addEventListener('click', () => {
    try {
      Characters.create({
        name: document.getElementById('new-name').value,
        gender: draft.gender,
        classId: draft.classId,
        specId: draft.specId,
        raceId: draft.raceId
      });
      closeModals();
      currentView = 'planner';
      render();
    } catch (error) {
      const box = document.getElementById('character-error');
      box.textContent = error.message;
      box.hidden = false;
    }
  });

  // Picker modal.
  const picker = document.getElementById('picker-modal');
  picker.addEventListener('click', event => {
    if (event.target === picker || event.target.closest('[data-close-modal]')) { closeModals(); return; }

    if (event.target.closest('#picker-clear')) {
      Characters.equip(Characters.active(), phaseId, pickerSlot, null);
      closeModals();
      render();
      return;
    }

    const row = event.target.closest('[data-equip]');
    if (row) equipItem(Number(row.dataset.equip));
  });

  const enchantModal = document.getElementById('enchant-modal');
  enchantModal.addEventListener('click', event => {
    if (event.target === enchantModal || event.target.closest('[data-close-modal]')) { closeModals(); return; }

    if (event.target.closest('#enchant-clear')) {
      Characters.setEnchant(Characters.active(), phaseId, enchantSlot, null);
      closeModals();
      render();
      return;
    }

    const option = event.target.closest('[data-enchant]');
    if (!option) return;
    Characters.setEnchant(Characters.active(), phaseId, enchantSlot, option.dataset.enchant);
    closeModals();
    render();
  });

  document.getElementById('enchant-search').addEventListener('input', renderEnchantRows);

  for (const id of ['picker-search', 'picker-quality', 'picker-source', 'picker-usable']) {
    const control = document.getElementById(id);
    control.addEventListener(control.tagName === 'INPUT' && control.type !== 'checkbox' ? 'input' : 'change',
      renderPickerRows);
  }

  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') { closeModals(); closeDropdowns(); }
  });
}

/**
 * The hit ceiling auto-fill should respect: the cap for whatever this loadout fights, less the hit
 * talents and enchants already provide. PvE gears against bosses, PvP against players.
 */
function hitBudgetFor(character) {
  const spec = specOf(character);
  const profile = hitProfile(
    data, data.hitcaps, character, spec, {}, Characters.talentsFor(character),
    Characters.weaponSkill(character, data.hitcaps.weaponSkill?.base ?? 300),
    Characters.enchantsFor(character, phaseId));

  const statKey = profile.kind === 'spell' ? 'spellHit' : 'hit';

  // A spec that places no value on hit must not be driven to a cap. Healing spells cannot miss, so
  // a Restoration shaman needs none at all - and the gap pass, which buys hit until the cap is met,
  // does not know that on its own. It bought 16% spell hit and spent a healer's gear on it.
  if (!spec.weights?.[statKey]) return null;

  const targetId = Characters.activeMode() === 'pvp' ? 'pvp' : 'boss';
  const target = profile.targets.find(t => t.id === targetId) ?? profile.targets[0];

  return {
    statKey,
    cap: target.cap,
    // White swings while dual wielding carry a further penalty, so hit past the yellow cap is not
    // dead for those specs - only worth less. A single cliff at the yellow cap threw away real
    // value for Fury, Combat, Assassination and Subtlety.
    whiteCap: spec.weaponStyle === 'dualwield' ? target.dualWieldWhiteCap ?? null : null,
    // Talents and enchants are already banked before a single item is chosen.
    alreadyHave: profile.total
  };
}

function adjustTalent(button, direction) {
  const character = Characters.active();
  const treeIndex = Number(button.dataset.tree);
  const cell = Number(button.dataset.cell);

  const talent = treesFor(data.talents, character.classId)[treeIndex]?.talents.find(t => t.cell === cell);
  if (!talent) return;

  const build = Characters.talentsFor(character);
  const next = direction > 0
    ? addPoint(data.talents, character.classId, build, treeIndex, talent)
    : removePoint(data.talents, character.classId, build, treeIndex, talent);

  if (next === build) return; // the rules refused the change
  Characters.setTalents(character, next);
  render();
}

function toggleAlternatives(toggle) {
  const expanded = toggle.getAttribute('aria-expanded') === 'true';
  toggle.setAttribute('aria-expanded', String(!expanded));

  let row = toggle.closest('tr').nextElementSibling;
  while (row && row.classList.contains('row-alt')) {
    row.classList.toggle('is-visible', !expanded);
    row = row.nextElementSibling;
  }
  toggle.textContent = expanded
    ? toggle.textContent.replace('Hide', 'Show')
    : toggle.textContent.replace('Show', 'Hide');
}

// ---- Start -----------------------------------------------------------------------------------

async function start() {
  try {
    data = await loadAll();
  } catch (error) {
    view.innerHTML = `<p class="error">Could not load the data files: ${esc(error.message)}.<br>
      If you opened this file directly, serve it instead — <code>dotnet run --project tools/OctoBis.Serve</code>.</p>`;
    return;
  }

  // Land on the phase that is actually live.
  phaseId = [...data.phases].reverse().find(p => p.status === 'live')?.id ?? 0;

  if (stamp) {
    stamp.textContent =
      `${data.meta.counts.items} items indexed · data built ${new Date(data.generated).toLocaleString()}`;
  }

  wire();
  // Tooltips read the equipped set at hover time so an item's set block can show how many
  // pieces you already have, and light up the bonuses you have reached.
  attachTooltips(document.body, data, equippedIds);
  render();
}

start();
