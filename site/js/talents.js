// The talent calculator: three trees per class, with vanilla's point rules enforced.

import { esc } from './render.js';

/** A level-60 character has 51 points to spend. */
export const TOTAL_POINTS = 51;

/** Each row down a tree needs five more points spent in that tree to unlock. */
const POINTS_PER_TIER = 5;

const TREE_COLUMNS = 4;

export function treesFor(talents, classId) {
  return talents?.classes?.[classId]?.trees ?? [];
}

export function spentIn(build, treeIndex) {
  return Object.entries(build)
    .filter(([key]) => key.startsWith(`${treeIndex}:`))
    .reduce((sum, [, ranks]) => sum + ranks, 0);
}

/**
 * Points spent in the rows above a given row - which is what a tier requirement actually counts.
 * A talent never pays for its own tier.
 */
export function spentBelowRow(build, treeIndex, tree, row) {
  let sum = 0;
  for (const talent of tree.talents) {
    if (talent.row >= row) continue;
    sum += build[`${treeIndex}:${talent.cell}`] ?? 0;
  }
  return sum;
}

export function totalSpent(build) {
  return Object.values(build).reduce((sum, ranks) => sum + ranks, 0);
}

export function ranksOf(build, treeIndex, cell) {
  return build[`${treeIndex}:${cell}`] ?? 0;
}

/**
 * Why a talent cannot take another point, or null if it can.
 * Returned as a message so the UI can explain the block rather than just refusing.
 */
export function blockedReason(talents, classId, build, treeIndex, talent) {
  if (ranksOf(build, treeIndex, talent.cell) >= talent.ranks) return 'Already at maximum rank.';
  if (totalSpent(build) >= TOTAL_POINTS) return `All ${TOTAL_POINTS} points are spent.`;

  const tier = Math.floor(talent.row);
  const needed = tier * POINTS_PER_TIER;
  const spent = spentIn(build, treeIndex);
  if (spent < needed) return `Requires ${needed} points in this tree — you have ${spent}.`;

  if (talent.requires !== null && talent.requires !== undefined) {
    const trees = treesFor(talents, classId);
    const prerequisite = trees[treeIndex]?.talents.find(t => t.cell === talent.requires);
    const required = talent.reqRanks ?? prerequisite?.ranks ?? 1;
    const have = ranksOf(build, treeIndex, talent.requires);
    if (have < required) {
      return `Requires ${required} point${required === 1 ? '' : 's'} in ${prerequisite?.name ?? 'its prerequisite'}.`;
    }
  }

  return null;
}

/**
 * Removing a point is only allowed when nothing else still depends on it — either a talent that
 * names it as a prerequisite, or a lower tier that would fall below its point requirement.
 */
export function canRemove(talents, classId, build, treeIndex, talent) {
  if (ranksOf(build, treeIndex, talent.cell) === 0) return false;

  const trees = treesFor(talents, classId);
  const tree = trees[treeIndex];
  if (!tree) return false;

  const after = { ...build, [`${treeIndex}:${talent.cell}`]: ranksOf(build, treeIndex, talent.cell) - 1 };

  for (const other of tree.talents) {
    const otherRanks = ranksOf(after, treeIndex, other.cell);
    if (otherRanks === 0) continue;

    if (other.requires === talent.cell) {
      const required = other.reqRanks ?? talent.ranks;
      if (ranksOf(after, treeIndex, talent.cell) < required) return false;
    }

    // Spending would drop below what this talent's tier demands. Only points in the rows below
    // it count towards its requirement - counting the dependent talent's own rank let a build
    // untrain into a state blockedReason itself refuses to build ("Requires 10 points - you have 9").
    if (spentBelowRow(after, treeIndex, tree, other.row) < other.row * POINTS_PER_TIER) return false;
  }

  return true;
}

export function addPoint(talents, classId, build, treeIndex, talent) {
  if (blockedReason(talents, classId, build, treeIndex, talent)) return build;
  const key = `${treeIndex}:${talent.cell}`;
  return { ...build, [key]: (build[key] ?? 0) + 1 };
}

export function removePoint(talents, classId, build, treeIndex, talent) {
  if (!canRemove(talents, classId, build, treeIndex, talent)) return build;

  const key = `${treeIndex}:${talent.cell}`;
  const next = { ...build, [key]: build[key] - 1 };
  if (next[key] === 0) delete next[key];
  return next;
}

// ---- Rendering -------------------------------------------------------------------------------

function talentCell(talents, classId, build, tree, talent, iconBase, iconExtension) {
  const ranks = ranksOf(build, tree.index, talent.cell);
  const blocked = blockedReason(talents, classId, build, tree.index, talent);

  // Available means "you could put a point in right now"; maxed keeps its bright state.
  const maxed = ranks >= talent.ranks;
  const state = maxed ? 'is-maxed' : ranks > 0 ? 'is-partial' : blocked ? 'is-locked' : 'is-available';

  const icon = talent.icon
    ? `<img src="${esc(iconBase + talent.icon + iconExtension)}" alt="" loading="lazy" width="40" height="40">`
    : '';

  const tip = [
    talent.name,
    talent.description ?? '',
    blocked && ranks === 0 ? `\n${blocked}` : ''
  ].filter(Boolean).join('\n');

  return `
    <button class="talent ${state}" type="button"
            style="grid-row:${talent.row + 1};grid-column:${talent.col + 1}"
            data-tree="${tree.index}" data-cell="${talent.cell}" title="${esc(tip)}">
      ${icon}
      <span class="talent-ranks">${ranks}/${talent.ranks}</span>
    </button>`;
}

export function renderTalents(talents, classId, build, spec) {
  const trees = treesFor(talents, classId);
  if (trees.length === 0) {
    return `<p class="empty">No talent data for this class. Re-run the scraper with
      <code>--talents</code> to fetch it.</p>`;
  }

  const spent = totalSpent(build);
  const remaining = TOTAL_POINTS - spent;

  return `
    <div class="talents">
      <div class="talents-head">
        <div>
          <h2>Talents</h2>
          <p class="panel-sub">Left-click to add a point, right-click to remove one.</p>
        </div>
        <div class="points ${remaining === 0 ? 'is-spent' : ''}">
          <span class="points-value">${remaining}</span>
          <span class="points-label">points left</span>
          <button class="button" type="button" id="reset-talents">Reset</button>
        </div>
      </div>

      <div class="tree-grid">
        ${trees.map(tree => `
          <section class="tree${spec && spec.name === tree.name ? ' is-spec' : ''}">
            <header class="tree-head">
              <h3>${esc(tree.name)}</h3>
              <span class="tree-points">${spentIn(build, tree.index)}</span>
            </header>
            <div class="tree-body" style="grid-template-columns:repeat(${TREE_COLUMNS}, 44px)">
              ${tree.talents.map(t =>
                talentCell(talents, classId, build, tree, t, talents.iconBase, talents.iconExtension)).join('')}
            </div>
          </section>`).join('')}
      </div>
    </div>`;
}
