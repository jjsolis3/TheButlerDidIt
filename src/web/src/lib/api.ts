import type {
  AiProviderKind,
  AiRole,
  AiStatus,
  ContentRating,
  GenerationJob,
  MediaJob,
  Me,
  MysteryLength,
  PartyInfo,
  PartyMode,
  PriceView,
  ProviderView,
  RoleView,
  SeatResponse,
  ThemeCard,
  UsageReport,
} from './types'

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
    let message = res.status === 401 ? 'Please sign in.' : `Request failed (${res.status}).`
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
  register: (email: string, password: string, displayName: string) =>
    request<Me>('POST', '/api/auth/register', { email, password, displayName }),
  logout: () => request<void>('POST', '/api/auth/logout'),

  themes: () => request<ThemeCard[]>('GET', '/api/themes'),

  myParties: () => request<PartyInfo[]>('GET', '/api/parties'),
  createParty: (scenarioId: string, mode: PartyMode, contentLevel: ContentRating, scheduledFor: string | null, useAi = true, drinkingPrompts = false) =>
    request<PartyInfo>('POST', '/api/parties', { scenarioId, mode, contentLevel, scheduledFor, useAi, drinkingPrompts }),

  // ---- Media
  partyMedia: (code: string) => request<{ ready: number; job: MediaJob | null }>('GET', `/api/parties/${encodeURIComponent(code)}/media`),
  prepareMedia: (code: string) => request<MediaJob>('POST', `/api/parties/${encodeURIComponent(code)}/media`),
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
    providers: () => request<ProviderView[]>('GET', '/api/admin/ai/providers'),
    createProvider: (p: { name: string; kind: AiProviderKind; baseUrl: string | null; apiKey: string | null }) =>
      request<ProviderView>('POST', '/api/admin/ai/providers', p),
    updateProvider: (id: string, p: { name: string; kind: AiProviderKind; baseUrl: string | null; apiKey: string | null }) =>
      request<ProviderView>('PUT', `/api/admin/ai/providers/${id}`, p),
    deleteProvider: (id: string) => request<void>('DELETE', `/api/admin/ai/providers/${id}`),
    testProvider: (id: string, model: string) =>
      request<{ ok: boolean; message: string; milliseconds: number }>('POST', `/api/admin/ai/providers/${id}/test`, { model }),
    roles: () => request<RoleView[]>('GET', '/api/admin/ai/roles'),
    setRole: (role: AiRole, providerId: string, model: string, maxOutputTokens: number | null) =>
      request<void>('PUT', `/api/admin/ai/roles/${role}`, { providerId, model, maxOutputTokens, temperature: null }),
    clearRole: (role: AiRole) => request<void>('DELETE', `/api/admin/ai/roles/${role}`),
    prices: () => request<PriceView[]>('GET', '/api/admin/ai/prices'),
    setPrice: (p: PriceView) => request<void>('PUT', '/api/admin/ai/prices', p),
    deletePrice: (model: string) => request<void>('DELETE', `/api/admin/ai/prices/${encodeURIComponent(model)}`),
    usage: (months = 3) => request<UsageReport>('GET', `/api/admin/ai/usage?months=${months}`),
  },
}
