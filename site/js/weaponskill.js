// The weapon-skill toggle, shared by the planner and the Hit Values tab.
//
// It lives on both because it belongs to both questions. On Hit Values it explains what the quest
// is worth; on the planner it is a control you reach for while building a set, since flipping it
// changes what auto-fill goes shopping for.

import { esc } from './render.js';

/**
 * @param {object} config    hitcaps.json
 * @param {object} profile   the hit profile for the current character
 * @param {'full'|'compact'} variant
 *        full    - heading, explanation and the quest name, for the Hit Values tab
 *        compact - one line and the toggle, for the planner's summary panel
 */
export function weaponSkillPanel(config, profile, variant = 'full') {
  // Weapon skill does nothing for a caster's spell hit, so it is not worth the space.
  if (profile.kind !== 'melee') return '';

  const settings = config.weaponSkill ?? {};
  const base = settings.base ?? 300;
  const max = settings.max ?? 305;
  const trained = profile.weaponSkill >= max;
  const boss = profile.targets.find(t => t.id === 'boss');
  const saving = boss?.weaponSkillSaving ?? 0;

  const toggle = `
    <div class="ws-toggle" role="group" aria-label="Weapon skill">
      <button class="ws-button${trained ? '' : ' is-active'}" type="button"
              data-weapon-skill="${base}">${base}</button>
      <button class="ws-button${trained ? ' is-active' : ''}" type="button"
              data-weapon-skill="${max}">${max}</button>
    </div>`;

  if (variant === 'compact') {
    return `
      <section class="weapon-skill weapon-skill-compact">
        <div class="ws-head">
          <div>
            <h3>Weapon skill</h3>
            <p class="muted small">
              ${trained
                ? `Trained. Bosses need <strong>${saving}% less hit</strong> from gear.`
                : `Untrained. The Fray Island quest would save <strong>${saving}%</strong> against bosses.`}
            </p>
          </div>
          ${toggle}
        </div>
      </section>`;
  }

  return `
    <section class="weapon-skill">
      <div class="ws-head">
        <div>
          <h3>Weapon skill</h3>
          <p class="muted small">${esc(settings.note ?? '')}</p>
        </div>
        ${toggle}
      </div>
      <p class="ws-why">
        ${trained
          ? `Counting the quest reward. Against bosses that is <strong>${saving}% less hit</strong>
             needed from gear than an untrained character.`
          : `Not counting the quest. Completing it would cut the hit you need against bosses by
             <strong>${saving}%</strong> — more than any single item gives you.`}
      </p>
      <p class="muted small">${esc(settings.questName ?? '')}</p>
    </section>`;
}
