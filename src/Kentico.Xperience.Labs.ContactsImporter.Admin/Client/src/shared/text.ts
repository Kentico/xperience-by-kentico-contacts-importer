import type { TextParams } from "./format";

/**
 * A text to show, as a resource key and its placeholder values. Code without React (options,
 * process, upload, worker) returns these; the components translate them with `useText().m`.
 * The strings are in `Resources/ContactsImporterClientResources.resx` (keys `contactsimporter.*`).
 */
export interface Message {
  readonly key: string;
  readonly params?: TextParams;
}
