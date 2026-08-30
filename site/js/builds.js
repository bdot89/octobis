// Suggested talent builds: one per class and spec, resolved from config/builds.json onto the
// real trees and offered on the Talents tab.
//
// The honesty of this feature lives in the wording, so it is worth stating here as well as in the
// config file: these are NOT popularity statistics. OctoWoW has no armory, no log aggregator and
// no per-spec guides, so nothing measures what people actually play. Each build is the settled
// Vanilla/Turtle build for its spec, translated onto OctoWoW's trees, and every place where that
// translation ran out is listed in 'diverges' and shown to the player rather than smoothed over.

import { esc } from './render.js';
import { treesFor, TOTAL_POINTS, spentIn, totalSpent } from './talents.js';

/**
 * Resolve a configured build into the same shape the calculator uses, `{ "treeIndex:cell": ranks }`.
 *
 * Names are resolved rather than trusted: a talent the trees no longer carry is dropped and
 * reported, so a server rename shows up as a visibly short build instead of a silent misallocation.
 *
 * @returns {{name, basis, diverges, build, spread, total, missing}|null}
 */
export function suggestedBuild(config, talents, classId, specId) {
  const entry = config?.builds?.[classId]?.[specId];
  if (!entry) return null;

  const trees = treesFor(talents, classId);
  if (trees.length === 0) return null;

  const build = {};
  const missing = [];

  for (const [treeName, points] of Object.entries(entry.points ?? {})) {
    const tree = trees.find(t => t.name === treeName);
    if (!tree) {
      missing.push(treeName);
      continue;
    }
    for (const [talentName, ranks] of Object.entries(points)) {
      const talent = tree.talents.find(t => t.name === talentName);
      if (!talent) {
        missing.push(`${talentName} (${treeName})`);
        continue;
      }
      // Clamp rather than trust: a rank the server has since reduced would otherwise produce a
      // build the calculator itself would refuse to accept.
      build[`${tree.index}:${talent.cell}`] = Math.min(ranks, talent.ranks);
    }
  }

  return {
    name: entry.name ?? 'Suggested build',
    basis: entry.basis ?? '',
    diverges: entry.diverges ?? [],
    build,
    spread: trees.map(tree => spentIn(build, tree.index)),
    total: totalSpent(build),
    missing
  };
}

/** True when the player's build already is the suggestion, so the panel can say so. */
export function matchesSuggestion(build, suggestion) {
  const mine = Object.entries(build).filter(([, ranks]) => ranks > 0);
  const theirs = Object.entries(suggestion.build).filter(([, ranks]) => ranks > 0);
  if (mine.length !== theirs.length) return false;
  return theirs.every(([key, ranks]) => build[key] === ranks);
}

/**
 * The panel shown above the trees.
 *
 * It leads with what the build is and what it descends from, then lists the divergences, because
 * those are the points a player should actually weigh rather than accept. Applying is one click
 * and fully reversible - the calculator's own rules still govern every edit afterwards.
 */
export function buildPanel(suggestion, currentBuild, talents, classId) {
  if (!suggestion) return '';

  const trees = treesFor(talents, classId);
  const applied = matchesSuggestion(currentBuild, suggestion);
  const spent = totalSpent(currentBuild);

  const columns = trees.map(tree => {
    const taken = tree.talents
      .map(talent => ({ talent, ranks: suggestion.build[`${tree.index}:${talent.cell}`] ?? 0 }))
      .filter(entry => entry.ranks > 0)
      .sort((a, b) => a.talent.row - b.talent.row || a.talent.col - b.talent.col);

    if (taken.length === 0) return '';

    return `
      <div class="sb-tree">
        <h4>${esc(tree.name)} <span class="sb-points">${suggestion.spread[tree.index]}</span></h4>
        <ul>
          ${taken.map(({ talent, ranks }) => `
            <li${ranks >= talent.ranks ? ' class="is-maxed"' : ''}>
              <span class="sb-name">${esc(talent.name)}</span>
              <span class="sb-rank">${ranks}/${talent.ranks}</span>
            </li>`).join('')}
        </ul>
      </div>`;
  }).join('');

  return `
    <section class="suggested-build${applied ? ' is-applied' : ''}">
      <div class="sb-head">
        <div>
          <h3>Suggested build <span class="sb-spread">${suggestion.spread.join(' / ')}</span></h3>
          <p class="muted small">${esc(suggestion.basis)}</p>
        </div>
        <div class="sb-actions">
          ${applied
            ? '<span class="sb-applied">This is your current build</span>'
            : `<button class="button button-primary" type="button" data-apply-build>
                 ${spent > 0 ? 'Replace my build' : 'Use this build'}
               </button>`}
        </div>
      </div>

      <p class="sb-provenance">
        Adapted from the settled Vanilla and Turtle WoW build for this spec — <strong>not</strong> a
        popularity ranking. OctoWoW publishes no armory or logs, so nothing measures what players
        actually pick here. Treat it as a starting point and argue with the points below.
      </p>

      <div class="sb-trees">${columns}</div>

      ${suggestion.diverges.length === 0 ? '' : `
        <div class="sb-diverges">
          <h4>Where OctoWoW differs from the vanilla build</h4>
          <ul>${suggestion.diverges.map(note => `<li>${esc(note)}</li>`).join('')}</ul>
        </div>`}

      ${suggestion.missing.length === 0 ? '' : `
        <p class="sb-missing">
          Not applied, because this class no longer has them:
          ${suggestion.missing.map(esc).join(', ')}.
        </p>`}

      ${suggestion.total === TOTAL_POINTS ? '' : `
        <p class="sb-missing">Spends ${suggestion.total} of ${TOTAL_POINTS} points.</p>`}
    </section>`;
}
