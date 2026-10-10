import { Limits } from '../types'
import { parseWholeNumber } from './options'

export type UploadCapCheck = { kBps: number; error: null } | { kBps: null; error: string }

/** The KB/s field of an upload cap: a whole number in the service's range (UploadCapManager.MinKBps..MaxKBps). */
export function checkUploadCapKBps(text: string): UploadCapCheck {
  const { min, max } = Limits.uploadCapKBps
  const kBps = parseWholeNumber(text)
  if (Number.isInteger(kBps) && kBps >= min && kBps <= max) return { kBps, error: null }
  return { kBps: null, error: `Enter a speed from ${min} to ${max.toLocaleString('en-US')} KB/s.` }
}

/** "1,000 KB/s": the number as the user typed it, the unit the service uses. */
export const formatKBps = (kBps: number) => `${kBps.toLocaleString('en-US')} KB/s`
