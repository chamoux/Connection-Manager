// Typed wrappers over the bridge, one per command the page may send.
import { call, PICKER_TIMEOUT_MS } from './bridge'
import type { ModeSetting, OptionsDto, PickResult, ReportDto, StatusDto, UploadCapArgs, UsageDto } from './types'

const done = (promise: Promise<unknown>) => promise.then(() => undefined)

export const api = {
  getStatus: () => call<StatusDto>('getStatus'),
  getUsage: () => call<UsageDto[] | null>('getUsage').then(list => list ?? []),
  getReports: () => call<ReportDto[] | null>('getReports').then(list => list ?? []),

  setGameMode: (mode: ModeSetting) => done(call('setGameMode', { mode })),
  setDataSaver: (mode: ModeSetting) => done(call('setDataSaver', { mode })),

  addBlock: (path: string, isFolder: boolean) => done(call('addBlock', { path, isFolder })),
  removeBlock: (path: string, isFolder: boolean) => done(call('removeBlock', { path, isFolder })),
  toggleCatalog: (id: string) => done(call('toggleCatalog', { id })),

  addGame: (path: string) => done(call('addGame', { path })),
  markNotAGame: (path: string) => done(call('markNotAGame', { path })),
  removeGame: (path: string, fromAlways: boolean) => done(call('removeGame', { path, isFolder: fromAlways })),

  allowApp: (path: string) => done(call('allowApp', { path })),
  removeAllowedApp: (path: string) => done(call('removeAllowedApp', { path })),
  setHotspot: (name: string, isHotspot: boolean) => done(call('setHotspot', { name, isHotspot })),
  setMetered: (on: boolean) => done(call('setMetered', { on })),

  setOptions: (options: OptionsDto) => done(call('setOptions', options)),
  dismissReport: (id: string) => done(call('dismissReport', { id })),
  restoreEverything: () => done(call('restoreEverything')),

  setUploadCap: (cap: UploadCapArgs) => done(call('setUploadCap', cap)),
  removeUploadCap: (path: string) => done(call('removeUploadCap', { path })),

  pickProgram: () => call<PickResult | null>('pickProgram', undefined, { timeoutMs: PICKER_TIMEOUT_MS }),
  pickFolder: () => call<PickResult | null>('pickFolder', undefined, { timeoutMs: PICKER_TIMEOUT_MS }),
  openLogFolder: () => done(call('openLogFolder')),
}
