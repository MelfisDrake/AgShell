// Ranking from agterm's Fuzzy.swift: a prefix beats a substring, which beats letters in order; lower is better.

export function fuzzyScore(query: string, target: string): number | null {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (terms.length === 0) {
    return 0;
  }
  const text = target.toLowerCase();
  let total = 0;
  for (const term of terms) {
    const score = termScore(term, text);
    if (score === null) {
      return null;
    }
    total += score;
  }
  return total;
}

export function fuzzyRank<T>(query: string, items: T[], keys: (item: T) => string[]): T[] {
  return items
    .map((item) => {
      const scores = keys(item)
        .map((key) => fuzzyScore(query, key))
        .filter((score): score is number => score !== null);
      return scores.length > 0 ? { item, score: Math.min(...scores), label: keys(item)[0] ?? "" } : null;
    })
    .filter((entry): entry is { item: T; score: number; label: string } => entry !== null)
    .sort((a, b) => a.score - b.score || a.label.localeCompare(b.label, undefined, { sensitivity: "accent" }))
    .map((entry) => entry.item);
}

function termScore(term: string, target: string): number | null {
  if (target.startsWith(term)) {
    return 0;
  }
  const index = target.indexOf(term);
  if (index >= 0) {
    // The substring band stays under the subsequence floor of 40.
    return 5 + Math.min(index, 34);
  }
  let position = 0;
  for (const char of target) {
    if (char === term[position]) {
      position++;
      if (position === term.length) {
        return 40 + (target.length - term.length);
      }
    }
  }
  return null;
}
