import { type ApiResult, requestJson } from './client';

/**
 * The co-pilot chat CRUD + turn client (gh#18, gh#1063, R-6) — `/conversations`. Mirrors
 * `MarqSpec.TradingCopilot.Api.Chat.ChatContracts`; those endpoints predate `openapi/v1.json` regeneration (they are
 * not in the generated {@link ../api/schema}), so the shapes are named here by hand, the same convention `ai.ts` and
 * `triggers.ts` already use for anonymous-object responses.
 *
 * **Grounding is invisible on the wire.** The always-on news grounding (gh#995, ADR-0027) that shapes an assistant
 * reply is server-internal — the retrieved items are handed to the model as untrusted user-role content and never
 * returned to the client as a separate structured field. So a message here is exactly what it looks like: an
 * operator or co-pilot turn of plain text, nothing more. A message's `content` — from EITHER role — is **untrusted
 * display data**: render it as text, never interpolate it into a prompt or treat it as instruction.
 */

/**
 * Who authored a message. Mirrors `ChatRole`. `Unknown` is the refusable zero — a DB check refuses it server-side,
 * so a real message is always `User`, `Assistant` or `System`. Enums cross the wire as **numbers** (no
 * `JsonStringEnumConverter` server-side), the same convention `triggers.ts` and `onboarding.ts` document.
 */
export const ChatRole = {
  Unknown: 0,
  User: 1,
  Assistant: 2,
  /** Grounding / instructions — never appended by this client and not rendered as an operator-visible turn. */
  System: 3,
} as const;
export type ChatRoleValue = (typeof ChatRole)[keyof typeof ChatRole];

/** The server's cap on a conversation title. Mirrors `Conversation.TitleMaxLength`. */
export const TITLE_MAX_LENGTH = 256;

/** The server's cap on one message's content. Mirrors `ChatMessage.ContentMaxLength`. */
export const CONTENT_MAX_LENGTH = 32_768;

/** A conversation as the operator sees it in a list -- no messages (see {@link ConversationDetail}). */
export interface Conversation {
  readonly id: string;
  readonly title: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/** One message in a conversation, in `sequence` order. `content` is untrusted display data (see the module note). */
export interface ChatMessage {
  readonly id: string;
  readonly conversationId: string;
  readonly sequence: number;
  readonly role: ChatRoleValue;
  readonly content: string;
  readonly createdAt: string;
}

/** A conversation with its messages, already ordered by `sequence`. */
export interface ConversationDetail extends Conversation {
  readonly messages: readonly ChatMessage[];
}

/** The wire envelope `GET /conversations` returns; {@link listConversations} unwraps it. */
interface ConversationListResponse {
  readonly conversations: readonly Conversation[];
}

/**
 * The result of a successful chat turn: the operator's persisted turn and the co-pilot's reply, both with their
 * allocated sequence. Mirrors `ChatTurnResponse`. A refused / faulted turn never reaches this shape -- it comes back
 * as an `ApiResult` refusal instead (the user's turn is still saved server-side; see {@link sendChatTurn}).
 */
export interface ChatTurnResult {
  readonly userMessage: ChatMessage;
  readonly assistantMessage: ChatMessage;
}

/**
 * Starts a conversation. `title` is optional -- a conversation may start untitled and be named later. Answers the
 * created row (200), or a **refusal** when the title exceeds {@link TITLE_MAX_LENGTH} (the server's 400).
 */
export function createConversation(title?: string): Promise<ApiResult<Conversation>> {
  return requestJson<Conversation>('POST', '/conversations', { title: title ?? null });
}

/**
 * Lists the operator's conversations, most-recent-first (the server orders by `updatedAt` desc). Owner-scoped
 * server-side (R-20); this client adds no filter of its own. `limit` is optional -- the server defaults and clamps
 * its own page size.
 */
export async function listConversations(
  limit?: number,
): Promise<ApiResult<readonly Conversation[]>> {
  const path = limit === undefined ? '/conversations' : `/conversations?limit=${limit}`;
  const result = await requestJson<ConversationListResponse>('GET', path);
  if (!result.ok) {
    return result;
  }
  return { ok: true, data: result.data.conversations };
}

/**
 * Reads one conversation and its messages, in order. A conversation that does not exist, or belongs to another
 * operator (R-20), is a **404** -- surfaced as a refusal, never disclosed as "not found vs. not yours".
 */
export function getConversation(id: string): Promise<ApiResult<ConversationDetail>> {
  return requestJson<ConversationDetail>('GET', `/conversations/${id}`);
}

/**
 * Takes a grounded co-pilot chat turn: appends the operator's message, runs the model over the thread (grounded in
 * news, gh#995), and appends the reply -- all in the one call, which the server does not answer until the whole
 * turn completes. There is no separate "send my message" step; the request carries only the text (never a role or
 * owner -- the server stamps both).
 *
 * **Streaming.** This call blocks for the full turn, but the server also pushes each token delta over the realtime
 * hub as it generates (`RealtimeContextValue.onChatChunk`) -- a caller renders those for an incremental "typing"
 * view and then replaces the draft with this call's settled {@link ChatTurnResult} once it resolves, which is the
 * turn's source of truth (the chunk stream is presentation-only and best-effort).
 *
 * A **refused / faulted turn is a refusal**, never an invented reply. What each status persisted differs, and the
 * difference matters to a caller rendering an optimistic row:
 * - **429** (the AI-spend governor) -- nothing persisted, the turn never ran.
 * - **409, turn already in flight** (gh#1106) -- nothing persisted either: the per-conversation guard wraps the
 *   operator-turn write, so a refused turn contributes nothing to the thread.
 * - **409, lost sequence race** -- the operator's turn IS saved when the collision was on the ASSISTANT append
 *   (the turn ran; a concurrent `POST /messages` took the position), and is NOT when the collision was on the
 *   operator append itself. This client cannot tell the two apart from the status.
 * - **422** (the model call failed / was refused) -- the operator's turn IS still saved server-side.
 *
 * So a caller that needs to know whether its own turn persisted re-reads with {@link getConversation} rather than
 * assuming either way. The two 409 causes are not distinguished by STATUS, but the in-flight one names itself in
 * the refusal envelope's `layer` ({@link TURN_IN_FLIGHT_LAYER}) — which a caller rendering the live chat draft
 * must key on, because the two demand opposite treatment of it (see {@link TURN_IN_FLIGHT_LAYER}).
 */
/**
 * The refusal envelope's `layer` for the one-in-flight-turn guard (gh#1106). Mirrors
 * `ChatEndpoints.TurnInFlightLayer`, the same way {@link CONTENT_MAX_LENGTH} mirrors its server constant.
 *
 * **Why a caller must key on it.** `POST /turns` has two 409s that demand opposite handling of the live draft, and
 * the status cannot tell them apart. This one means **this connection's turn never ran** — and it is returned
 * *precisely when another turn is streaming*, so the draft on screen belongs to that turn: it must not be retired,
 * and the deltas still arriving must not be suppressed. The other 409 (a lost sequence race on the assistant
 * append) means this connection's turn DID run, so its draft is finished and its stragglers are suppressible.
 */
export const TURN_IN_FLIGHT_LAYER = 'chat-turn-in-flight';

export function sendChatTurn(id: string, content: string): Promise<ApiResult<ChatTurnResult>> {
  return requestJson<ChatTurnResult>('POST', `/conversations/${id}/turns`, { content });
}
