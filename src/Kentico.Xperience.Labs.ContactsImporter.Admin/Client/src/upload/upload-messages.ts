import type { Message } from "../shared/text";
import { workerErrorMessage } from "../worker/messages";

import type { UploadSessionError } from "./upload-session";

/** Text of a failed upload pass (`UploadSessionError` other than `aborted`). */
export function uploadFailureMessage(failure: UploadSessionError): Message {
  const server = failure.serverMessage;
  switch (failure.kind) {
    case "rejected":
      if (failure.reason === "uploadTooLarge") {
        return { key: "contactsimporter.upload.errors.toolarge" };
      }
      if (!server) {
        return { key: "contactsimporter.upload.errors.rejectednodetail" };
      }

      return failure.rowNumber !== undefined
        ? {
            key: "contactsimporter.upload.errors.rejectedrow",
            params: { reason: server, row: failure.rowNumber },
          }
        : {
            key: "contactsimporter.upload.errors.rejected",
            params: { reason: server },
          };
    case "network":
      return { key: "contactsimporter.upload.errors.network" };
    case "noRows":
      return { key: "contactsimporter.upload.errors.norows" };
    case "complete":
      return server
        ? {
            key: "contactsimporter.upload.errors.complete",
            params: { reason: server },
          }
        : { key: "contactsimporter.upload.errors.completenodetail" };
    default:
      return workerErrorMessage(failure.workerError ?? failure, "upload");
  }
}
