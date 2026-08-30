// Hit caps: how much you need for each kind of target, how much you have, and what to do when
// you have too much.
//
// Hit is the one stat with a hard ceiling. Below the cap it is usually a spec's best stat; a single
// point above it is worth exactly nothing. That makes "you are over the cap" the most actionable
// thing a gear planner can tell someone, which is why it gets its own view.

import { scoreItem } from './score.js';
import { candidatesFor, allowedByStyle } from './equipped.js';
import { appliedHit } from './enchants.js';
import { ALL_SLOTS, contextFor, slotByKey, isTwoHanded } from './slots.js';

/** Which of the two caps a spec actually gears for. */
export function hitKindFor(spec) {
  return spec?.school ? 'spell' : 'melee';
}

/** Hit percentage the character's talent build contributes. */
export function talentHit(config, talents, classId, spec, build) {
  const trees = talents?.classes?.[classId]?.trees ?? [];
  const kind = hitKindFor(spec);
  const contributions = [];

  for (const tree of trees) {
    for (const talent of tree.talents) {
      const ranks = build[`${tree.index}:${talent.cell}`] ?? 0;
      if (ranks === 0) continue;

      const entries = config.talents[talent.name];
      if (!entries) continue;

      for (const entry of entries) {
        if (entry.class !== classId) continue;
        if (entry.type !== 'both' && entry.type !== kind) continue;
        // School-limited talents only count for a spec that casts that school.
        if (entry.schools && !entry.schools.includes(spec?.school)) continue;

        contributions.push({
          name: talent.name,
          tree: tree.name,
          ranks,
          value: ranks * entry.perRank
        });
      }
    }
  }

  return {
    total: contributions.reduce((sum, c) => sum + c.value, 0),
    contributions
  };
}

/** Hit percentage the equipped gear contributes, and which pieces provide it. */
export function gearHit(data, gear, kind) {
  const statKey = kind === 'spell' ? 'spellHit' : 'hit';
  const pieces = [];

  for (const [slotKey, itemId] of Object.entries(gear)) {
    const item = data.byId.get(itemId);
    const value = item?.stats?.[statKey];
    if (!value) continue;
    pieces.push({ slotKey, slotName: slotByKey(slotKey)?.name ?? slotKey, item, value });
  }

  return {
    total: pieces.reduce((sum, p) => sum + p.value, 0),
    pieces: pieces.sort((a, b) => b.value - a.value)
  };
}

/**
 * The melee cap for a given weapon skill.
 *
 * The config states the caps at the skill values that matter (300 and 305); anything between is
 * interpolated, and past the top entry the returns flatten to a per-point rate.
 */
export function meleeCapFor(target, weaponSkill, config) {
  const table = target.meleeCapBySkill ?? {};
  const keys = Object.keys(table).map(Number).sort((a, b) => a - b);
  if (keys.length === 0) return target.meleeCap ?? 0;

  const lowest = keys[0];
  const highest = keys[keys.length - 1];

  if (weaponSkill <= lowest) return table[lowest];

  if (weaponSkill >= highest) {
    const perPoint = config.weaponSkill?.perPointAbove305 ?? 0;
    return round(Math.max(0, table[highest] - (weaponSkill - highest) * perPoint));
  }

  // Between two stated values: straight line between them.
  const upper = keys.find(k => k >= weaponSkill);
  const lower = [...keys].reverse().find(k => k <= weaponSkill);
  if (lower === upper) return table[lower];

  const ratio = (weaponSkill - lower) / (upper - lower);
  return round(table[lower] + (table[upper] - table[lower]) * ratio);
}

/**
 * The full picture: what you have, and how it lands against each target.
 */
export function hitProfile(data, config, character, spec, gear, talentBuild, weaponSkill, enchants) {
  const kind = hitKindFor(spec);
  const skill = weaponSkill ?? config.weaponSkill?.base ?? 300;

  const fromTalents = talentHit(config, data.talents, character.classId, spec, talentBuild);
  const fromGear = gearHit(data, gear, kind);
  const fromEnchants = appliedHit(data.enchantIndex, enchants, kind);
  const total = round(fromGear.total + fromTalents.total + fromEnchants.total);

  // Talents and enchants are fixed once your build is set; gear is the part you actually shop for.
  const fromEquipment = round(fromGear.total + fromEnchants.total);

  const targets = config.targets.map(target => {
    const cap = kind === 'spell' ? target.spellCap : meleeCapFor(target, skill, config);
    const difference = round(total - cap);

    // The figure that matters when choosing items: what is left after talents.
    const neededFromGear = round(Math.max(0, cap - fromTalents.total));

    // What capping the weapon skill quest would be worth against this target.
    const baseCap = kind === 'spell' ? target.spellCap : meleeCapFor(target, config.weaponSkill?.base ?? 300, config);
    const maxCap = kind === 'spell' ? target.spellCap : meleeCapFor(target, config.weaponSkill?.max ?? 305, config);

    return {
      ...target,
      cap,
      difference,
      neededFromGear,
      haveFromGear: fromEquipment,
      talentHit: round(fromTalents.total),
      status: difference < 0 ? 'under' : difference === 0 ? 'exact' : 'over',
      note: kind === 'spell' ? target.spellNote : target.dualWieldNote,
      weaponSkillSaving: round(baseCap - maxCap)
    };
  });

  // Healing spells cannot miss. A spec that puts no weight on hit does not have a hit cap to reach,
  // and telling one it is 16% short would send a healer shopping for a stat that does nothing.
  const statKey = kind === 'spell' ? 'spellHit' : 'hit';
  const needsHit = Boolean(spec?.weights?.[statKey]);

  return { kind, needsHit, total, fromGear, fromTalents, fromEnchants, fromEquipment, targets, weaponSkill: skill };
}

/**
 * Which equipped pieces to replace when hit is over the cap.
 *
 * Surplus hit is scored at zero, so a candidate is compared on what it is actually worth to you
 * rather than on its raw stat line. Only swaps that come out ahead are reported.
 */
export function replacementAdvice(data, character, spec, classDef, phaseId, overrides, gear, profile, target) {
  const kind = profile.kind;
  const statKey = kind === 'spell' ? 'spellHit' : 'hit';
  const hitWeight = spec.weights?.[statKey] ?? 0;

  if (target.status !== 'over' || hitWeight === 0) return [];

  const suggestions = [];

  for (const piece of profile.fromGear.pieces) {
    const slot = slotByKey(piece.slotKey);
    if (!slot) continue;

    // Hit the rest of the set already provides, so each candidate can be judged on the total.
    const hitElsewhere = round(profile.total - piece.value);

    const effective = (item, rawScore) => {
      const own = item.stats?.[statKey] ?? 0;
      const surplus = Math.max(0, hitElsewhere + own - target.cap);
      const wasted = Math.min(own, surplus);
      return { score: rawScore - hitWeight * wasted, wasted, own };
    };

    const context = contextFor(slot);
    const currentRaw = scoreItem(piece.item, spec, overrides, context);
    const current = effective(piece.item, currentRaw);

    // Nothing to gain if this piece wastes no hit at all.
    if (current.wasted <= 0) continue;

    const candidates = candidatesFor(data, character, spec, classDef, phaseId, piece.slotKey, overrides)
      .filter(c => c.item.id !== piece.item.id
                   && !Object.values(gear).includes(c.item.id));

    let best = null;
    for (const candidate of candidates) {
      const evaluated = effective(candidate.item, candidate.score);
      if (evaluated.score <= current.score) continue;
      if (!best || evaluated.score > best.evaluated.score) best = { candidate, evaluated };
    }

    if (!best) continue;

    suggestions.push({
      slotKey: piece.slotKey,
      slotName: piece.slotName,
      current: piece.item,
      currentWasted: round(current.wasted),
      replacement: best.candidate.item,
      replacementSources: best.candidate.sources,
      gain: Math.round(best.evaluated.score - current.score),
      hitDelta: round((best.evaluated.own) - piece.value),
      changes: statChanges(piece.item, best.candidate.item, spec)
    });
  }

  // Biggest win first - that is the swap worth making today.
  return suggestions.sort((a, b) => b.gain - a.gain);
}

/** The stats that actually moved, largest weighted change first, for explaining a swap. */
function statChanges(from, to, spec) {
  const weights = spec.weights ?? {};
  const keys = new Set([...Object.keys(from.stats ?? {}), ...Object.keys(to.stats ?? {})]);
  const changes = [];

  for (const key of keys) {
    const delta = round((to.stats?.[key] ?? 0) - (from.stats?.[key] ?? 0));
    if (delta === 0) continue;
    changes.push({ key, delta, weighted: Math.abs(delta * (weights[key] ?? 0)) });
  }

  return changes.sort((a, b) => b.weighted - a.weighted).slice(0, 6);
}

/** Slots that could still supply hit, for when a character is short of the cap. */
export function bestHitUpgrades(data, character, spec, classDef, phaseId, overrides, gear, profile, target) {
  if (target.status !== 'under') return [];

  const statKey = profile.kind === 'spell' ? 'spellHit' : 'hit';
  const upgrades = [];

  for (const slot of ALL_SLOTS) {
    // The advice has to be as legal as auto-fill's own picks. Without these it proposed an off
    // hand to someone holding a two-hander, a two-hander to a Protection warrior, and the same
    // ring for both fingers.
    if (slot.key === 'offhand' && isTwoHanded(data.byId.get(gear.mainhand))) continue;

    const equippedId = gear[slot.key];
    const equipped = equippedId ? data.byId.get(equippedId) : null;
    const currentHit = equipped?.stats?.[statKey] ?? 0;

    const candidates = candidatesFor(data, character, spec, classDef, phaseId, slot.key, overrides)
      .filter(c => (c.item.stats?.[statKey] ?? 0) > currentHit
                   && c.item.id !== equippedId
                   && allowedByStyle(slot, c.item, spec)
                   && !Object.values(gear).includes(c.item.id));

    if (candidates.length === 0) continue;

    // The most hit available in this slot, preferring the higher-scoring item when tied.
    const best = candidates.reduce((a, b) => {
      const aHit = a.item.stats[statKey], bHit = b.item.stats[statKey];
      if (bHit !== aHit) return bHit > aHit ? b : a;
      return b.score > a.score ? b : a;
    });

    upgrades.push({
      slotKey: slot.key,
      slotName: slot.name,
      current: equipped,
      currentHit,
      replacement: best.item,
      replacementSources: best.sources,
      hitGain: round(best.item.stats[statKey] - currentHit)
    });
  }

  return upgrades.sort((a, b) => b.hitGain - a.hitGain).slice(0, 6);
}

function round(value) {
  return Math.round(value * 100) / 100;
}
