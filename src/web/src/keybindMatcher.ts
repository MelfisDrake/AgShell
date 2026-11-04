import type { KeyBindingView, KeyChordView } from "./bridge";

export interface BoundKey {
  binding: KeyBindingView;
}

export function sameChord(a: KeyChordView, b: KeyChordView): boolean {
  return !!a.meta === !!b.meta && a.ctrl === b.ctrl && a.alt === b.alt && a.shift === b.shift && a.code === b.code;
}

function sameSequence(a: KeyChordView[], b: KeyChordView[]): boolean {
  return a.length === b.length && a.every((chord, index) => sameChord(chord, b[index]));
}

export class KeybindMatcher {
  pending: KeyChordView[] = [];
  repeatPrefix: KeyChordView[] | null = null;

  reset(): void {
    this.pending = [];
    this.repeatPrefix = null;
  }

  repeatTail<T extends BoundKey>(chord: KeyChordView, binds: T[]): T | undefined {
    const prefix = this.repeatPrefix;
    return prefix ? binds.find(({ binding }) => binding.repeat && sameSequence(binding.sequence, [...prefix, chord])) : undefined;
  }

  advance<T extends BoundKey>(chord: KeyChordView, binds: T[]): T | "armed" | null {
    if (this.repeatPrefix) {
      const repeated = this.repeatTail(chord, binds);
      if (repeated) return repeated;
      this.repeatPrefix = null;
    }

    const sequence = [...this.pending, chord];
    const exact = binds.find(({ binding }) => sameSequence(binding.sequence, sequence));
    if (exact) {
      this.pending = [];
      this.repeatPrefix = exact.binding.repeat && sequence.length > 1 ? sequence.slice(0, -1) : null;
      return exact;
    }

    if (binds.some(({ binding }) => binding.sequence.length > sequence.length && sequence.every((part, index) => sameChord(part, binding.sequence[index])))) {
      this.pending = sequence;
      return "armed";
    }

    if (this.pending.length) {
      this.pending = [];
      return this.advance(chord, binds);
    }

    return null;
  }
}
