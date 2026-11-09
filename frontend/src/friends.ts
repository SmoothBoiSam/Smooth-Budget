import type { Friend } from './api'
import { formatDay } from './format'

export const friendField = 'block w-full rounded-xl neu-inset px-3 py-2.5 text-base outline-none focus:ring-2 focus:ring-accent'
export const friendSaveBtn = 'shrink-0 rounded-xl bg-primary px-4 text-sm font-semibold text-primary-ink shadow-neu-sm disabled:opacity-40'

/** One line on where things stand with a friend: linked, invited, or just a name; and whether their transfers are tracked. */
export function friendStatus(f: Friend) {
  const account = f.linked
    ? `Linked account${f.linkedSince ? ` since ${formatDay(f.linkedSince.slice(0, 10))}` : ''}`
    : f.inviteExpiresUtc
      ? `Invite sent, works until ${formatDay(f.inviteExpiresUtc.slice(0, 10))}`
      : 'No account'
  return f.transferName ? `${account} · transfers tracked` : account
}
