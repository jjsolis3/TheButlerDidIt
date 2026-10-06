import type {
  AdminGameRow,
  AdminOverview,
  SignUpsView,
  EscapeDifficulty,
  AiProviderKind,
  AiRole,
  AiStatus,
  ContentRating,
  EscapeRoomSummary,
  Leaderboard,
  PuzzleChoice,
  HostPreferences,
  SeatFeedbackView,
  FeedbackRequest,
  InsightsView,
  GenerationJob,
  MediaJob,
  Me,
  MyMystery,
  ValidationResult,
  RecapPage,
  RecapSharing,
  RoomMediaView,
  MysteryMediaView,
  EscapeLibraryItem,
  SharingRequest,
  EscapeRecapPage,
  EscapeRecapSharing,
  SpectatorList,
  WatchResponse,
  AccessView,
  AccountView,
  AuthOptions,
  CreatedInvite,
  EmailChangeResult,
  HostView,
  InviteInfo,
  InviteView,
  MysteryLength,
  PartyInfo,
  PartyMode,
  PriceView,
  ProviderView,
  RoleView,
  SeatResponse,
  ThemeCard,
  Tone,
  UsageReport,
} from './types'
import type { EditableRoom, EscapeRoomDoc, SavedRoom } from './escapeDoc'
import type { ScenarioDoc } from './scenarioDoc'

/** The optional choices when creating a party. Named options, so a call reads as what it asks for. */
export interface CreatePartyOptions {
  useAi?: boolean
  drinkingPrompts?: boolean
  version?: string | null
  tone?: Tone
  /** With version "surprise": if no version's killer is a guest, let the AI write one. */
  tailorWithAi?: boolean
}

/** An error whose message came from the server and is safe to show to the user. */
export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

/**
 * Sends a file as the request body, reporting how much has gone so far (0 to 1). `fetch` can't report upload
 * progress, so this uses the older XMLHttpRequest, wrapped in a Promise so callers can `await` it like the rest.
 */
function upload<T>(url: string, file: File, onProgress?: (fraction: number) => void): Promise<T> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open('POST', url)
    xhr.responseType = 'json' // problem details on failure, the result on success
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable) onProgress?.(e.loaded / e.total)
    }
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) return resolve(xhr.response as T)
      const problem = xhr.response as { detail?: string; title?: string } | null
      const fallback = xhr.status === 401 ? 'Please sign in.' : xhr.status === 413 ? 'That file is too big.' : `Upload failed (${xhr.status}).`
      reject(new ApiError(xhr.status, problem?.detail ?? problem?.title ?? fallback))
    }
    xhr.onerror = () => reject(new ApiError(0, 'The upload was cut off. Check the connection and try again.'))
    // The file itself is the body (not a form): the browser streams it from disk and sets its type.
    xhr.send(file)
  })
}

/** A media key as a URL path: "clue/c1/ab12" stays three segments, each encoded. */
const keyPath = (key: string) => key.split('/').map(encodeURIComponent).join('/')

async function request<T>(method: string, url: string, body?: unknown, seatToken?: string): Promise<T> {
  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  // A guest's calls carry their seat token instead of a sign-in (as the phones' SignalR connection does).
  if (seatToken) headers['X-Seat-Token'] = seatToken
  const res = await fetch(url, {
    method,
    // Same origin, so the host's auth cookie is sent automatically.
    credentials: 'same-origin',
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!res.ok) {
    // The API returns RFC 7807 "problem details": { title, detail, status }.
    let message =
      res.status === 401
        ? 'Please sign in.'
        : res.status === 429
          ? 'Too many tries from this network in the last minute. Wait a moment and try again.'
          : `Request failed (${res.status}).`
    try {
      const problem = await res.json()
      message = problem.detail ?? problem.title ?? message
    } catch {
      /* not JSON */
    }
    throw new ApiError(res.status, message)
  }
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}

export const api = {
  me: () => request<Me>('GET', '/api/auth/me'),
  login: (email: string, password: string) => request<Me>('POST', '/api/auth/login', { email, password }),
  /** `invite` is the token from an invite link, needed while sign-ups are closed. */
  register: (email: string, password: string, displayName: string, invite?: string) =>
    request<Me>('POST', '/api/auth/register', { email, password, displayName, invite }),
  checkInvite: (token: string) => request<InviteInfo>('POST', '/api/auth/invite', { token }),
  logout: () => request<void>('POST', '/api/auth/logout'),
  authOptions: () => request<AuthOptions>('GET', '/api/auth/options'),
  forgotPassword: (email: string) => request<{ message: string }>('POST', '/api/auth/forgot', { email }),
  resetPassword: (email: string, token: string, password: string) => request<Me>('POST', '/api/auth/reset', { email, token, password }),
  confirmEmail: (userId: string, token: string) => request<void>('POST', '/api/auth/confirm', { userId, token }),
  resendConfirmation: () => request<void>('POST', '/api/auth/resend-confirmation'),

  /** The signed-in host's own account. Every call acts on whoever is signed in. */
  account: {
    get: () => request<AccountView>('GET', '/api/account'),
    rename: (displayName: string) => request<Me>('PUT', '/api/account/profile', { displayName }),
    changeEmail: (newEmail: string, password: string) => request<EmailChangeResult>('POST', '/api/account/email', { newEmail, password }),
    confirmEmailChange: (userId: string, email: string, token: string) => request<Me>('POST', '/api/account/email/confirm', { userId, email, token }),
    changePassword: (currentPassword: string, newPassword: string) => request<void>('POST', '/api/account/password', { currentPassword, newPassword }),
    signOutEverywhere: () => request<void>('POST', '/api/account/sign-out-everywhere'),
    /** The host's usual party settings (#102). */
    preferences: () => request<HostPreferences>('GET', '/api/account/preferences'),
    savePreferences: (p: HostPreferences) => request<HostPreferences>('PUT', '/api/account/preferences', p),
    /** A file download, so it's a plain link rather than a fetch. */
    exportUrl: '/api/account/export',
    remove: (password: string) => request<void>('POST', '/api/account/delete', { password }),
  },

  themes: () => request<ThemeCard[]>('GET', '/api/themes'),
  escapeRooms: () => request<EscapeRoomSummary[]>('GET', '/api/escape-rooms'),
  generateEscapeRoom: (theme: string, contentRating: ContentRating, minutes: number) =>
    request<GenerationJob>('POST', '/api/escape-rooms/generate', { theme, contentRating, minutes }),
  deleteEscapeRoom: (id: string) => request<void>('DELETE', `/api/escape-rooms/${encodeURIComponent(id)}`),
  // My escape rooms, and the admin's switches: share their own room with every host, or take a built-in one off the shelf.
  escapeLibrary: () => request<EscapeLibraryItem[]>('GET', '/api/escape-rooms/library'),
  shareEscapeRoom: (id: string, change: SharingRequest) => request<void>('PUT', `/api/escape-rooms/${encodeURIComponent(id)}/sharing`, change),
  // The escape room editor (#113).
  escapeRoomDocument: (id: string) => request<EditableRoom>('GET', `/api/escape-rooms/${encodeURIComponent(id)}/document`),
  validateEscapeRoom: (document: EscapeRoomDoc) => request<ValidationResult>('POST', '/api/escape-rooms/validate', { document }),
  saveEscapeRoom: (id: string, document: EscapeRoomDoc) => request<SavedRoom>('PUT', `/api/escape-rooms/${encodeURIComponent(id)}/document`, { document }),
  duplicateEscapeRoom: (id: string) => request<{ id: string }>('POST', `/api/escape-rooms/${encodeURIComponent(id)}/duplicate`),
  createEscapeParty: (
    roomId: string,
    mode: PartyMode,
    puzzles: PuzzleChoice,
    puzzleSet: number | null,
    useAi: boolean,
    minutes: number | null,
    difficulty: EscapeDifficulty = 'normal',
  ) => request<PartyInfo>('POST', '/api/parties/escape', { roomId, mode, puzzles, puzzleSet, useAi, minutes, difficulty }),
  leaderboard: (roomId: string, daily: boolean, minutes: number, party?: string, difficulty: EscapeDifficulty = 'normal') =>
    request<Leaderboard>(
      'GET',
      `/api/escape-rooms/${encodeURIComponent(roomId)}/leaderboard?daily=${daily}&minutes=${minutes}&difficulty=${difficulty}${party ? `&party=${encodeURIComponent(party)}` : ''}`,
    ),

  myParties: () => request<PartyInfo[]>('GET', '/api/parties'),
  // The content level isn't sent: the server uses the mystery's own rating.
  // version: 'surprise' lets the server pick one this host hasn't played; a version id picks it; null plays the original.
  createParty: (scenarioId: string, mode: PartyMode, scheduledFor: string | null, options: CreatePartyOptions = {}) =>
    request<PartyInfo>('POST', '/api/parties', { scenarioId, mode, scheduledFor, useAi: true, drinkingPrompts: false, version: null, tone: 'standard', tailorWithAi: false, ...options }),
  // Unfinished parties are deleted; finished ones are only hidden from the list (their recap keeps working).
  removeParty: (code: string) => request<void>('DELETE', `/api/parties/${encodeURIComponent(code)}`),

  // ---- Media
  partyMedia: (code: string) => request<{ ready: number; job: MediaJob | null }>('GET', `/api/parties/${encodeURIComponent(code)}/media`),
  prepareMedia: (code: string) => request<MediaJob>('POST', `/api/parties/${encodeURIComponent(code)}/media`),
  recap: (code: string) => request<RecapSharing>('GET', `/api/parties/${encodeURIComponent(code)}/recap`),
  shareRecap: (code: string) => request<{ url: string }>('POST', `/api/parties/${encodeURIComponent(code)}/recap/share`),
  unshareRecap: (code: string) => request<void>('DELETE', `/api/parties/${encodeURIComponent(code)}/recap/share`),
  publicRecap: (slug: string) => request<RecapPage>('GET', `/api/recap/${encodeURIComponent(slug)}`),
  // Spectator mode (#112): watch a party's TV without a seat; the host sees who's watching and can stop them.
  watch: (code: string, name: string) => request<WatchResponse>('POST', `/api/parties/${encodeURIComponent(code)}/watch`, { name }),
  spectators: (code: string) => request<SpectatorList>('GET', `/api/parties/${encodeURIComponent(code)}/spectators`),
  removeSpectator: (code: string, id: string) => request<void>('DELETE', `/api/parties/${encodeURIComponent(code)}/spectators/${id}`),
  allowSpectators: (code: string, allow: boolean) => request<SpectatorList>('PUT', `/api/parties/${encodeURIComponent(code)}/spectators`, { allow }),
  // Escape rooms share their recap the same way (shareRecap / unshareRecap); only the page differs.
  escapeRecap: (code: string) => request<EscapeRecapSharing>('GET', `/api/parties/${encodeURIComponent(code)}/escape-recap`),
  publicEscapeRecap: (slug: string) => request<EscapeRecapPage>('GET', `/api/escape-recap/${encodeURIComponent(slug)}`),
  myMysteries: () => request<MyMystery[]>('GET', '/api/scenarios/mine'),
  scenario: (id: string) => request<{ id: string; source: MyMystery['source']; canEdit: boolean; document: ScenarioDoc }>('GET', `/api/scenarios/${encodeURIComponent(id)}`),
  validateScenario: (document: ScenarioDoc) => request<ValidationResult>('POST', '/api/scenarios/validate', { document }),
  // A room's own pictures, videos and sounds (#110 step 2). Each change applies at once and returns the room's media again.
  roomMedia: (id: string) => request<RoomMediaView>('GET', `/api/escape-rooms/${encodeURIComponent(id)}/media`),
  uploadRoomMedia: (id: string, key: string, file: File, onProgress?: (fraction: number) => void) =>
    upload<RoomMediaView>(`/api/escape-rooms/${encodeURIComponent(id)}/media/${encodeURIComponent(key)}`, file, onProgress),
  removeRoomMedia: (id: string, key: string) =>
    request<RoomMediaView>('DELETE', `/api/escape-rooms/${encodeURIComponent(id)}/media/${encodeURIComponent(key)}`),
  // A mystery's own pictures, videos and music, played in every version of it. Keys have slashes ("portrait/finch"),
  // which stay as they are; each part is encoded on its own.
  mysteryMedia: (id: string) => request<MysteryMediaView>('GET', `/api/scenarios/${encodeURIComponent(id)}/media`),
  uploadMysteryMedia: (id: string, key: string, file: File, onProgress?: (fraction: number) => void) =>
    upload<MysteryMediaView>(`/api/scenarios/${encodeURIComponent(id)}/media/${keyPath(key)}`, file, onProgress),
  removeMysteryMedia: (id: string, key: string) => request<MysteryMediaView>('DELETE', `/api/scenarios/${encodeURIComponent(id)}/media/${keyPath(key)}`),
  saveScenario: (id: string, document: ScenarioDoc) => request<ValidationResult>('PUT', `/api/scenarios/${encodeURIComponent(id)}`, { document }),
  duplicateScenario: (id: string) => request<{ id: string }>('POST', `/api/scenarios/${encodeURIComponent(id)}/duplicate`),
  deleteScenario: (id: string) => request<void>('DELETE', `/api/scenarios/${encodeURIComponent(id)}`),
  shareScenario: (id: string, change: SharingRequest) => request<void>('PUT', `/api/scenarios/${encodeURIComponent(id)}/sharing`, change),
  kitUrl: (code: string, kind: 'invitations' | 'booklets' | 'nametags' | 'clues') => `/api/parties/${encodeURIComponent(code)}/kit/${kind}.pdf`,

  /** Upload a costume selfie. Uses the seat token, because guests don't have accounts. */
  uploadPhoto: async (token: string, file: File) => {
    const form = new FormData()
    form.append('photo', file)
    const res = await fetch('/api/seat/photo', { method: 'POST', headers: { 'X-Seat-Token': token }, body: form })
    if (!res.ok) {
      let message = `Upload failed (${res.status}).`
      try {
        const problem = await res.json()
        message = problem.detail ?? problem.title ?? message
      } catch {
        /* not JSON */
      }
      throw new ApiError(res.status, message)
    }
    return (await res.json()) as { photoUrl: string }
  },
  removePhoto: (token: string) => fetch('/api/seat/photo', { method: 'DELETE', headers: { 'X-Seat-Token': token } }),
  /** A guest's verdict on the game, once it's over (#130). */
  seatFeedback: (token: string) => request<SeatFeedbackView>('GET', '/api/seat/feedback', undefined, token),
  sendFeedback: (token: string, feedback: FeedbackRequest) => request<SeatFeedbackView>('POST', '/api/seat/feedback', feedback, token),
  /** What happened in every game of a mystery or room, for its owner or the admin (#130). */
  insights: (kind: 'mystery' | 'escape', id: string) => request<InsightsView>('GET', `/api/insights/${kind}/${encodeURIComponent(id)}`),
  party: (code: string) => request<PartyInfo>('GET', `/api/parties/${encodeURIComponent(code)}`),
  join: (code: string, name: string) => request<SeatResponse>('POST', `/api/parties/${encodeURIComponent(code)}/join`, { name }),
  addSeat: (code: string, name: string, isLocal: boolean) =>
    request<SeatResponse>('POST', `/api/parties/${encodeURIComponent(code)}/seats`, { name, isLocal }),

  // ---- AI
  aiStatus: () => request<AiStatus>('GET', '/api/ai/status'),
  generate: (themeSlug: string, players: number, contentRating: ContentRating, length: MysteryLength, twist: string) =>
    request<GenerationJob>('POST', '/api/generation', { themeSlug, players, contentRating, length, twist: twist || null }),
  generationJob: (id: string) => request<GenerationJob>('GET', `/api/generation/${id}`),

  admin: {
    // The admin hub (#102): the overview, every game with how it plays, and the sign-up switch.
    overview: () => request<AdminOverview>('GET', '/api/admin/overview'),
    games: () => request<AdminGameRow[]>('GET', '/api/admin/games'),
    signUps: () => request<SignUpsView>('GET', '/api/admin/signups'),
    /** true: anyone may sign up; false: invites only; null: as the server's configuration says. */
    setSignUps: (open: boolean | null) => request<SignUpsView>('PUT', '/api/admin/signups', { open }),
    hosts: () => request<HostView[]>('GET', '/api/admin/hosts'),
    resetLink: (id: string) => request<{ link: string; validForHours: number }>('POST', `/api/admin/hosts/${encodeURIComponent(id)}/reset-link`),
    giveFreeAccess: (id: string) => request<AccessView>('POST', `/api/admin/hosts/${encodeURIComponent(id)}/free-access`),
    removeFreeAccess: (id: string) => request<AccessView>('DELETE', `/api/admin/hosts/${encodeURIComponent(id)}/free-access`),
    invites: () => request<InviteView[]>('GET', '/api/admin/invites'),
    createInvite: (i: { email: string | null; note: string | null; days: number; send: boolean; freeAccess: boolean }) =>
      request<CreatedInvite>('POST', '/api/admin/invites', i),
    deleteInvite: (id: string) => request<void>('DELETE', `/api/admin/invites/${encodeURIComponent(id)}`),
    providers: () => request<ProviderView[]>('GET', '/api/admin/ai/providers'),
    createProvider: (p: { name: string; kind: AiProviderKind; baseUrl: string | null; apiKey: string | null }) =>
      request<ProviderView>('POST', '/api/admin/ai/providers', p),
    updateProvider: (id: string, p: { name: string; kind: AiProviderKind; baseUrl: string | null; apiKey: string | null }) =>
      request<ProviderView>('PUT', `/api/admin/ai/providers/${id}`, p),
    deleteProvider: (id: string) => request<void>('DELETE', `/api/admin/ai/providers/${id}`),
    testProvider: (id: string, model: string) =>
      request<{ ok: boolean; message: string; milliseconds: number }>('POST', `/api/admin/ai/providers/${id}/test`, { model }),
    roles: () => request<RoleView[]>('GET', '/api/admin/ai/roles'),
    setRole: (role: AiRole, providerId: string, model: string, maxOutputTokens: number | null, claude?: { effort: string | null; refusalFallbackModel: string | null }) =>
      request<void>('PUT', `/api/admin/ai/roles/${role}`, {
        providerId,
        model,
        maxOutputTokens,
        temperature: null,
        effort: claude?.effort ?? null,
        refusalFallbackModel: claude?.refusalFallbackModel ?? null,
      }),
    clearRole: (role: AiRole) => request<void>('DELETE', `/api/admin/ai/roles/${role}`),
    prices: () => request<PriceView[]>('GET', '/api/admin/ai/prices'),
    setPrice: (p: PriceView) => request<void>('PUT', '/api/admin/ai/prices', p),
    deletePrice: (model: string) => request<void>('DELETE', `/api/admin/ai/prices/${encodeURIComponent(model)}`),
    usage: (months = 3) => request<UsageReport>('GET', `/api/admin/ai/usage?months=${months}`),
  },
}
