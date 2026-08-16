// The per-character guide. Right now that means hit: what each kind of target needs, what your
// gear and talents actually give you, and what to do about the gap either way.

import { esc, statLine, sourceLine, statLabel as label } from './render.js';
import { iconUrl } from './data.js';
import { hitProfile, replacementAdvice, bestHitUpgrades } from './hit.js';

function signed(value) {
  return value > 0 ? `+${value}` : `${value}`;
}

function itemChip(data, item) {
  const icon = iconUrl(data, item);
  return `
    <span class="chip">
      ${icon ? `<img src="${esc(icon)}" alt="" width="20" height="20">` : ''}
      <span class="item-name q-${['poor','common','uncommon','rare','epic','legendary'][item.quality] ?? 'common'}"
            data-item="${item.id}">${esc(item.name)}</span>
    </span>`;
}

function weaponSkillPanel(config, profile, spec) {
  // Weapon skill does nothing for a caster's spell hit, so it is not worth the space.
  if (profile.kind !== 'melee') return '';

  const settings = config.weaponSkill ?? {};
  const trained = profile.weaponSkill >= (settings.max ?? 305);
  const boss = profile.targets.find(t => t.id === 'boss');

  return `
    <section class="weapon-skill">
      <div class="ws-head">
        <div>
          <h3>Weapon skill</h3>
          <p class="muted small">${esc(settings.note ?? '')}</p>
        </div>
        <div class="ws-toggle" role="group" aria-label="Weapon skill">
          <button class="ws-button${trained ? '' : ' is-active'}" type="button"
                  data-weapon-skill="${settings.base ?? 300}">${settings.base ?? 300}</button>
          <button class="ws-button${trained ? ' is-active' : ''}" type="button"
                  data-weapon-skill="${settings.max ?? 305}">${settings.max ?? 305}</button>
        </div>
      </div>
      <p class="ws-why">
        ${trained
          ? `Counting the quest reward. Against bosses that is <strong>${boss?.weaponSkillSaving ?? 0}% less hit</strong>
             needed from gear than an untrained character.`
          : `Not counting the quest. Completing it would cut the hit you need against bosses by
             <strong>${boss?.weaponSkillSaving ?? 0}%</strong> — more than any single item gives you.`}
      </p>
      <p class="muted small">${esc(settings.questName ?? '')}</p>
    </section>`;
}

/**
 * A target card leads with the figure a player actually shops against: how much hit gear has to
 * supply once talents are counted. The raw cap is shown as supporting detail — a Shadow priest
 * needs 6% from gear, not the 16% headline, and quoting 16% at them is just misleading.
 */
function targetCard(target, profile) {
  const { status, cap, difference, neededFromGear, haveFromGear, talentHit } = target;

  const verdict = {
    under: `<strong>${Math.abs(difference)}% short.</strong> Every point of hit until then is
            worth more than almost anything else you could gear for.`,
    exact: '<strong>Exactly capped.</strong> Any further hit is wasted.',
    over: `<strong>${difference}% over.</strong> That surplus does nothing — it is the same as
           having empty stats on those items.`
  }[status];

  const pct = neededFromGear === 0
    ? 100
    : Math.min(100, (haveFromGear / neededFromGear) * 100);

  return `
    <section class="hit-card is-${status}">
      <header>
        <h3>${esc(target.name)}</h3>
        <span class="hit-figures"><strong>${haveFromGear}%</strong> / ${neededFromGear}%</span>
      </header>
      <div class="hit-bar"><span style="width:${pct}%"></span></div>
      <p class="hit-need">
        Gear must supply <strong>${neededFromGear}%</strong>
        ${talentHit > 0
          ? `<span class="muted">— ${cap}% needed in total, ${talentHit}% of it from talents</span>`
          : `<span class="muted">— nothing in this build reduces it</span>`}
      </p>
      <p class="hit-verdict">${verdict}</p>
      <p class="hit-blurb">${esc(target.blurb)}</p>
      ${target.note ? `<p class="hit-note">${esc(target.note)}</p>` : ''}
    </section>`;
}

function sourceBreakdown(data, profile) {
  const { fromGear, fromTalents } = profile;
  const kindName = profile.kind === 'spell' ? 'spell hit' : 'hit';

  const gearRows = fromGear.pieces.length
    ? fromGear.pieces.map(p => `
        <li><span class="break-slot">${esc(p.slotName)}</span>
            ${itemChip(data, p.item)}
            <span class="break-value">+${p.value}%</span></li>`).join('')
    : '<li class="muted">No equipped item provides hit.</li>';

  const talentRows = fromTalents.contributions.length
    ? fromTalents.contributions.map(c => `
        <li><span class="break-slot">${esc(c.tree)}</span>
            <span>${esc(c.name)} <span class="muted">${c.ranks} point${c.ranks === 1 ? '' : 's'}</span></span>
            <span class="break-value">+${c.value}%</span></li>`).join('')
    : '<li class="muted">No talents in this build grant hit.</li>';

  return `
    <div class="breakdown">
      <section>
        <h3>From gear <span class="break-total">+${round(fromGear.total)}%</span></h3>
        <ul>${gearRows}</ul>
      </section>
      <section>
        <h3>From enchants <span class="break-total">+${round(profile.fromEnchants?.total ?? 0)}%</span></h3>
        <ul>${profile.fromEnchants?.pieces.length
          ? profile.fromEnchants.pieces.map(p => `
              <li><span class="break-slot">${esc(p.slotKey)}</span>
                  <span>${esc(p.name)}</span>
                  <span class="break-value">+${p.value}%</span></li>`).join('')
          : '<li class="muted">No applied enchant provides hit.</li>'}</ul>
      </section>
      <section>
        <h3>From talents <span class="break-total">+${round(fromTalents.total)}%</span></h3>
        <ul>${talentRows}</ul>
        <p class="muted small">Talent hit is read from your actual build, so respeccing changes
          the ${esc(kindName)} you need from gear.</p>
      </section>
    </div>`;
}

function replacementList(data, suggestions, target) {
  if (suggestions.length === 0) {
    return `<p class="muted">Nothing worth swapping — the surplus is spread across pieces that are
      still your best option even with the wasted hit.</p>`;
  }

  return `<ul class="swaps">${suggestions.map(s => `
    <li class="swap">
      <div class="swap-head">
        <span class="break-slot">${esc(s.slotName)}</span>
        <span class="swap-gain">+${s.gain}</span>
      </div>
      <div class="swap-body">
        <div class="swap-side">
          <span class="swap-label">Replace</span>
          ${itemChip(data, s.current)}
          <span class="stat-line">${esc(statLine(s.current))}</span>
        </div>
        <span class="swap-arrow" aria-hidden="true">→</span>
        <div class="swap-side">
          <span class="swap-label">With</span>
          ${itemChip(data, s.replacement)}
          <span class="stat-line">${esc(statLine(s.replacement))}</span>
          <span class="swap-source">${sourceLine(s.replacementSources[0] ?? null)}</span>
        </div>
      </div>
      <p class="swap-why">
        ${esc(s.current.name)} wastes <strong>${s.currentWasted}%</strong> hit above the
        ${target.cap}% cap. Swapping ${s.hitDelta === 0 ? 'keeps hit level' : `changes hit by ${signed(s.hitDelta)}%`}
        and trades it for ${esc(describeChanges(s.changes))}.
      </p>
    </li>`).join('')}</ul>`;
}

function describeChanges(changes) {
  const meaningful = changes.filter(c => c.key !== 'hit' && c.key !== 'spellHit');
  if (meaningful.length === 0) return 'a straight reduction in wasted hit';

  return meaningful
    .slice(0, 4)
    .map(c => {
      const name = label(c.key);
      // Percentage stats read "+2% crit", not "+2 % crit".
      return name.startsWith('% ')
        ? `${signed(c.delta)}%${name.slice(1)}`
        : `${signed(c.delta)} ${name}`;
    })
    .join(', ');
}

function upgradeList(data, upgrades) {
  if (upgrades.length === 0) return '<p class="muted">No slot offers more hit than what you already have there.</p>';

  return `<ul class="swaps">${upgrades.map(u => `
    <li class="swap">
      <div class="swap-head">
        <span class="break-slot">${esc(u.slotName)}</span>
        <span class="swap-gain">+${u.hitGain}% hit</span>
      </div>
      <div class="swap-body">
        <div class="swap-side">
          <span class="swap-label">Currently</span>
          ${u.current ? itemChip(data, u.current) : '<span class="muted">Empty</span>'}
        </div>
        <span class="swap-arrow" aria-hidden="true">→</span>
        <div class="swap-side">
          <span class="swap-label">Most hit available</span>
          ${itemChip(data, u.replacement)}
          <span class="stat-line">${esc(statLine(u.replacement))}</span>
          <span class="swap-source">${sourceLine(u.replacementSources[0] ?? null)}</span>
        </div>
      </div>
    </li>`).join('')}</ul>`;
}

export function renderGuide(data, config, character, cls, spec, phase, gear, talentBuild, mode, context) {
  const profile = hitProfile(
    data, config, character, spec, gear, talentBuild, context.weaponSkill, context.enchants);

  // The target you gear for depends on the mode: PvP means players, PvE means bosses.
  const primaryId = mode === 'pvp' ? 'pvp' : 'boss';
  const primary = profile.targets.find(t => t.id === primaryId) ?? profile.targets[0];

  const suggestions = primary.status === 'over'
    ? replacementAdvice(data, character, spec, cls, phase.id, context.overrides, gear, profile, primary)
    : [];

  const upgrades = primary.status === 'under'
    ? bestHitUpgrades(data, character, spec, cls, phase.id, context.overrides, gear, profile, primary)
    : [];

  const kindName = profile.kind === 'spell' ? 'Spell hit' : 'Melee hit';

  return `
    <div class="guide">
      <header class="guide-head">
        <div>
          <h1>${esc(character.name)} <span class="muted">· ${esc(cls.name)} ${esc(spec.name)}</span></h1>
          <p class="panel-sub">${esc(mode === 'pvp' ? 'PvP' : 'PvE')} loadout · ${esc(phase.name)} ·
            gearing against <strong>${esc(primary.name)}</strong></p>
        </div>
        <div class="guide-total">
          <span class="score-value">${profile.total}%</span>
          <span class="score-label">${esc(kindName)} total</span>
          <span class="score-split">${profile.fromEquipment}% gear · ${round(profile.fromTalents.total)}% talents</span>
        </div>
      </header>

      ${Object.keys(gear).length === 0
        ? `<p class="phase-banner">Nothing is equipped yet, so this is showing talents only.
            Auto-fill a set on the Planner tab to see where your hit actually lands.</p>`
        : ''}

      ${weaponSkillPanel(config, profile, spec)}

      <div class="hit-cards">${profile.targets.map(t => targetCard(t, profile)).join('')}</div>

      <h2 class="guide-section">Where your ${esc(kindName.toLowerCase())} comes from</h2>
      ${sourceBreakdown(data, profile)}

      <h2 class="guide-section">
        ${primary.status === 'over' ? 'Gear wasting hit' : primary.status === 'under' ? 'Closing the gap' : 'You are capped'}
      </h2>
      ${primary.status === 'over'
        ? replacementList(data, suggestions, primary)
        : primary.status === 'under'
          ? upgradeList(data, upgrades)
          : '<p class="muted">Hit is exactly at the cap for this target. Spend everything else on throughput.</p>'}

      <p class="panel-note">
        Caps assume a level 60 with 300 weapon skill. Hit above a cap is worth nothing at all, which
        is why surplus is treated as a dead stat when ranking swaps.
      </p>
    </div>`;
}

function round(value) {
  return Math.round(value * 100) / 100;
}
