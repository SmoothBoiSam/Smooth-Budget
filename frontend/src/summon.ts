/** Who can visit a page (Lola.tsx): Lola, Draco, or the two of them meeting. */
export type PetVisit = 'lola' | 'meet' | 'draco'

// The "?" on More (DECISIONS.md 311): the next page that opens has one of them, whatever the dice say.
let summoned: PetVisit | null = null

export function summonNext() {
  const kinds: PetVisit[] = ['lola', 'draco', 'meet']
  summoned = kinds[Math.floor(Math.random() * kinds.length)]
}

/** The visit the "?" asked for, once: the page that takes it uses it up. */
export function takeSummoned() {
  const k = summoned
  summoned = null
  return k
}
