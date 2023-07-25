import React, { useState } from "react";
import {
  Input,
  UploadTile,
  ProgressBar,
  RadioGroup,
  NotificationBarAlert,
  RadioButton,
  Button,
  ButtonSize,
  Stack,
  Select,
  Callout,
  MenuItem,
  RadioGroupSize,
  Headline,
  HeadlineSize,
  CalloutType,
  CalloutPlacementType,
  Box,
  Spacing,
  UploadTileSize,
  Shelf,
  Column,
  Row,
  Cols,
  Divider,
  DividerOrientation,
} from "@kentico/xperience-admin-components";

/*
* This file demonstrates a custom UI page template.
  The template supports a single page command that retrieves a string value from the backend.

  In this example, the command retrieves the server's DateTime.Now value and displays it in a label.
  See ~\UIPages\CustomTemplate\CustomTemplate.cs for the backend definition of the page.
*/

interface CustomLayoutProps {
  readonly label: string;
  readonly contactGroups: { guid: string; displayName: string }[];
}

let canContinue = true;
let toofast = false;

export const ImportLayoutTemplate = ({
  label,
  contactGroups,
}: CustomLayoutProps) => {
  const [error, setError] = useState<string | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [state, setState] = useState<string[]>([]);
  const [currentFile, setCurrentFile] = useState({ current: 0, total: 0 });

  const [importKind, setImportKind] = useState("insert");
  const [contactGroup, setContactGroup] = useState<string | undefined>("");
  const [delimiter, setDelimiter] = useState<string>(",");
  const [batchSize, setBatchSize] = useState<number>(5000);

  const uploadModeOptions = [
    {
      label: "Insert (skip existing)",
      value: "insert",
    },
    {
      label: "Delete (delete existing)",
      value: "delete",
    },
  ];

  function parseFile(
    this: any,
    file: File,
    callback: (buffer: ArrayBuffer) => boolean,
    finishedCallback: () => void
  ) {
    var fileSize = file.size;
    var chunkSize = 32 * 1024; // bytes
    var offset = 0;
    var chunkReaderBlock:
      | null
      | ((_offset: number, length: number, _file: File) => void) = null;

    chunkReaderBlock = function (_offset: number, length: number, _file: File) {
      const r = new FileReader();
      const blob = _file.slice(_offset, length + _offset);
      r.onload = function (evt: ProgressEvent<FileReader>) {
        if (!canContinue) {
          return;
        }

        if (evt.target == null) {
          setError("An error occurred while reading the file.");

          return;
        }
        if (evt.target.error == null && evt.target.result != null) {
          // offset += (evt.target.result as any).length;
          offset += chunkSize;
          if (!callback(evt.target.result as ArrayBuffer)) {
            // callback for handling read chunk
            return;
          }
        } else {
          finishedCallback();
          setError(
            evt.target.error?.message ??
              "An error occurred while reading the file."
          );
          console.log("Read error: " + evt.target.error);
          return;
        }
        if (offset >= fileSize) {
          finishedCallback();
          setState((prev) => [...prev, "Completed reading file."]);
          return;
        }

        // of to the next chunk
        if (chunkReaderBlock != null && canContinue) {
          // console.log('reading next block');
          chunkReaderBlock(offset, chunkSize, file);
        }
      };
      if (toofast) {
        setTimeout(() => {
          r.readAsText(blob);
        }, 3000);
        setState((prev) => [
          ...prev,
          "Pausing upload while contacts are imported.",
        ]);
        toofast = false;
      } else {
        r.readAsText(blob);
      }
    };

    // now let's start the read with the first block
    chunkReaderBlock(offset, chunkSize, file);
  }

  function onUpload() {
    if (!file) {
      setError("No file was selected");

      return;
    }

    setState([]);

    const port = location.port != "" ? `:${location.port}` : "";
    const scheme = window.location.protocol === "http:" ? "ws" : "wss";
    const socket = new WebSocket(
      `${scheme}://${location.hostname}${port}/contactsimport/ws`
    );
    socket.binaryType = "blob";
    socket.onerror = (e) => {
      setError("An error occurred while uploading the file.");
      console.error(e);
      canContinue = false;
      socket.close();
    };
    socket.onmessage = (event) => {
      const p = JSON.parse(event.data);
      switch (p.type) {
        case "headerConfirmed": {
          setState((prev) => [
            ...prev,
            "CSV header validated. Starting import.",
          ]);

          parseFile(
            file,
            (buffer) => {
              if (socket.readyState == socket.OPEN) {
                socket.send(buffer);
                return true;
              } else {
                return false;
              }
            },
            () => {
              setTimeout(() => {
                socket.close();
              }, 2000);
            }
          );

          break;
        }
        case "toofast": {
          toofast = true;
          break;
        }
        case "msg": {
          setState((prev) => [...prev, p.payload]);
          break;
        }
        case "progress": {
          const len = parseInt(p.payload, 10);

          if (len === currentFile.total) {
            setState((prev) => [...prev, "Upload completed."]);
            setFile(null);
            canContinue = false;
            if (socket.readyState < socket.CLOSING) {
              socket.close();
            }
          }

          setCurrentFile((prev) => ({
            total: prev.total,
            current: prev.current + len,
          }));

          break;
        }
        case "finished": {
          setState((prev) => [...prev, "Upload completed."]);
          setFile(null);
          canContinue = false;
          if (socket.readyState < socket.CLOSING) {
            socket.close();
          }
          break;
        }
      }
    };
    socket.onopen = function (event) {
      setError("");
      setState((prev) => [
        ...prev,
        `Sending file of length: ${getFileSize(file)}`,
      ]);
      canContinue = true;

      socket.send(
        JSON.stringify({
          type: "header",
          payload: {
            importKind,
            contactGroup: !contactGroup ? null : contactGroup,
            delimiter,
            batchSize,
          },
        })
      );
    };
  }

  return (
    <Box spacing={Spacing.M}>
      <Headline size={HeadlineSize.L} spacingBottom={Spacing.M}>
        {label}
      </Headline>
      <Row spacing={Spacing.XL}>
        <Column
          cols={Cols.Col12}
          colsMd={Cols.Col10}
          colsLg={Cols.Col8}
          order={Cols.Col2}
          orderLg={Cols.Col1}
        >
          <Stack spacing={Spacing.XL} fullHeight={true}>
            <Callout
              type={CalloutType.QuickTip}
              placement={CalloutPlacementType.OnPaper}
              headline="Instructions"
              subheadline="Note"
            >
              <p>
                Use the options below to upload a CSV file containing your
                Contact records.
              </p>
            </Callout>

            {error && (
              <NotificationBarAlert onDismiss={() => {}}>
                {error}
              </NotificationBarAlert>
            )}

            <RadioGroup
              label="Select Upload Mode"
              name="uploadMode"
              size={RadioGroupSize.Medium}
              markAsRequired={true}
              value={importKind}
              onChange={setImportKind}
            >
              {uploadModeOptions.map((option, key) => (
                <RadioButton key={key} {...option}>
                  {option.label}
                </RadioButton>
              ))}
            </RadioGroup>

            <Select
              label="Assign to Contact Group"
              clearable={true}
              placeholder="Select Group"
              onChange={setContactGroup}
              value={contactGroup}
              disabled={!contactGroups.length}
            >
              {contactGroups.map((c) => (
                <MenuItem
                  primaryLabel={c.displayName}
                  key={c.guid}
                  value={c.guid}
                />
              ))}
            </Select>

            <UploadTile
              acceptFiles=".csv"
              firstLineLabel="Drag&Drop .csv here"
              secondLineLabel="or"
              buttonLabel="Browse"
              size={UploadTileSize.Compact}
              onUpload={([f]) => {
                if (f) {
                  setFile(f);
                  setCurrentFile({ current: 0, total: f.size });
                }
              }}
            />

            {file && (
              <p style={{ color: "var(--color-text-default-on-light)" }}>
                File Selected: {file.name} ({getFileSize(file)})
              </p>
            )}

            <Input
              label="CSV record delimeter"
              type="text"
              onChange={(v) => setDelimiter(v.target.value)}
              value={delimiter}
            />
            <Input
              label="Batch size"
              type="number"
              onChange={(v) => setBatchSize(parseInt(v.target.value, 10))}
              value={batchSize}
              min={1}
            />

            {currentFile.total > 0 && (
              <div>
                <Headline size={HeadlineSize.S} spacingBottom={Spacing.M}>
                  Upload Progress
                </Headline>
                <ProgressBar
                  completed={Math.floor(
                    (currentFile.current / currentFile.total) * 100
                  )}
                />
              </div>
            )}

            <Button
              label="Upload file"
              size={ButtonSize.S}
              onClick={onUpload}
            />

            <Divider orientation={DividerOrientation.Horizontal} />

            {state.length > 0 && (
              <Shelf>
                <Box spacing={Spacing.M}>
                  <Headline size={HeadlineSize.S}>Upload Log</Headline>
                  <pre style={{ color: "var(--color-text-default-on-light)" }}>
                    {[...state].reverse().join("\r\n")}
                  </pre>
                </Box>
              </Shelf>
            )}
          </Stack>
        </Column>
      </Row>
    </Box>
  );
};

function getFileSize(file: File): string {
  const fileSizeBytes = file.size;
  const fileSizeKB = fileSizeBytes / 1024;
  if (fileSizeKB < 0.01) {
    return msg(fileSizeBytes, "B");
  }

  const fileSizeMB = fileSizeKB / 1024;

  if (fileSizeMB < 0.01) {
    return msg(fileSizeKB, "KB");
  }

  const fileSizeGB = fileSizeMB / 1024;
  if (fileSizeGB < 0.01) {
    return msg(fileSizeMB, "MB");
  }

  return msg(fileSizeGB, "GB");

  function msg(size: number, unit: string) {
    return `${size.toFixed(2)} ${unit}`;
  }
}
