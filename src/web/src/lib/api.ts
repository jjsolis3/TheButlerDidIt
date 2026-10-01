import type {
  EscapeDifficulty,
  AiProviderKind,
  AiRole,
  AiStatus,
  ContentRating,
  EscapeRoomSummary,
  Leaderboard,
  PuzzleChoice,
  GenerationJob,
  MediaJob,
  Me,
  MyMystery,
  ValidationResult,
  RecapPage,
  RecapSharing,
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

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    // Same origin, so the host's auth cookie is sent automatically.
    credentials: 'same-origin',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
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
    /** A file download, so it's a plain link rather than a fetch. */
    exportUrl: '/api/account/export',
    remove: (password: string) => request<void>('POST', '/api/account/delete', { password }),
  },

  themes: () => request<ThemeCard[]>('GET', '/api/themes'),
  escapeRooms: () => request<EscapeRoomSummary[]>('GET', '/api/escape-rooms'),
  generateEscapeRoom: (theme: string, contentRating: ContentRating, minutes: number) =>
    request<GenerationJob>('POST', '/api/escape-rooms/generate', { theme, contentRating, minutes }),
  deleteEscapeRoom: (id: string) => request<void>('DELETE', `/api/escape-rooms/${encodeURIComponent(id)}`),
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
  myMysteries: () => request<MyMystery[]>('GET', '/api/scenarios/mine'),
  scenario: (id: string) => request<{ id: string; source: MyMystery['source']; canEdit: boolean; document: ScenarioDoc }>('GET', `/api/scenarios/${encodeURIComponent(id)}`),
  validateScenario: (document: ScenarioDoc) => request<ValidationResult>('POST', '/api/scenarios/validate', { document }),
  saveScenario: (id: string, document: ScenarioDoc) => request<ValidationResult>('PUT', `/api/scenarios/${encodeURIComponent(id)}`, { document }),
  duplicateScenario: (id: string) => request<{ id: string }>('POST', `/api/scenarios/${encodeURIComponent(id)}/duplicate`),
  deleteScenario: (id: string) => request<void>('DELETE', `/api/scenarios/${encodeURIComponent(id)}`),
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
    hosts: () => request<HostView[]>('GET', '/api/admin/hosts'),
    resetLink: (id: string) => request<{ link: string; validForHours: number }>('POST', `/api/admin/hosts/${encodeURIComponent(id)}/reset-link`),
    invites: () => request<InviteView[]>('GET', '/api/admin/invites'),
    createInvite: (i: { email: string | null; note: string | null; days: number; send: boolean }) => request<CreatedInvite>('POST', '/api/admin/invites', i),
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
