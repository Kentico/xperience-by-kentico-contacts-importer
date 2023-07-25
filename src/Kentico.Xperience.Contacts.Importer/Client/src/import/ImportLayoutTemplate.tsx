import React, { useState } from "react";
import { Input, UploadTile, ProgressBar, RadioGroup, RadioButton, Button, ButtonSize, Stack, Select, Callout,MenuItem, RadioGroupSize, Headline, HeadlineSize, CalloutType, CalloutPlacementType, Box, Spacing, UploadTileSize, TextWithLabel } from "@kentico/xperience-admin-components";


/*
* This file demonstrates a custom UI page template.
  The template supports a single page command that retrieves a string value from the backend.

  In this example, the command retrieves the server's DateTime.Now value and displays it in a label.
  See ~\UIPages\CustomTemplate\CustomTemplate.cs for the backend definition of the page.
*/

interface CustomLayoutProps {
  readonly label: string;
  readonly contactGroups: { guid: string, displayName: string }[];
}

let canContinue = true;
let toofast = false;

export const ImportLayoutTemplate = ({ label, contactGroups }: CustomLayoutProps) => {
  const [labelValue, _] = useState(label);

  const [file, setFile] = useState<File | null>(null);
  const [state, setState] = useState<string[]>([]);
  const [currentFile, setCurrentFile] = useState({ current: 0, total: 0 });

  const [importKind, setImportKind] = useState('insert');
  const [contactGroup, setContactGroup] = useState<string | undefined>(undefined);
  const [delimiter, setDelimiter] = useState<string>(',');
  const [batchSize, setBatchSize] = useState<number>(5000);
  // const [blockCache, setBlockCache] = useState<number>(1500);

  const options = [{
    label: 'Insert (skip existing)',
    value: 'insert'
  }, {
    label: 'Delete (delete existing)',
    value: 'delete'
  }];


  function parseFile(this: any, file: File, callback: (buffer: ArrayBuffer) => boolean, finishedCallback: () => void) {
    var fileSize = file.size;
    var chunkSize = 32 * 1024; // bytes
    var offset = 0;
    var self = this as unknown; // we need a reference to the current object
    var chunkReaderBlock: null | ((_offset: number, length: number, _file: File) => void) = null;

    var readEventHandler = function (evt: ProgressEvent<FileReader>) {
      if (!canContinue) {
        return;
      }

      if (evt.target == null) {
        console.log('progress is null');
        return;
      }
      if (evt.target.error == null && evt.target.result != null) {
        // offset += (evt.target.result as any).length;
        offset += chunkSize;
        if (!callback(evt.target.result as ArrayBuffer)) // callback for handling read chunk
        {
          return;
        }
      } else {
        finishedCallback();
        console.log("Read error: " + evt.target.error);
        return;
      }
      if (offset >= fileSize) {
        finishedCallback();
        console.log("Done reading file");
        return;
      }

      // of to the next chunk
      if (chunkReaderBlock != null && canContinue) {
        // console.log('reading next block');
        chunkReaderBlock(offset, chunkSize, file);
      }
    }

    chunkReaderBlock = function (_offset: number, length: number, _file: File) {
      var r = new FileReader();
      var blob = _file.slice(_offset, length + _offset);
      r.onload = readEventHandler;
      if (toofast) {
        setTimeout(() => { r.readAsText(blob); }, 3000);
        console.log('too fast => wait 3s');
        toofast = false;
      }
      else {
        r.readAsText(blob);
      }
    }

    // now let's start the read with the first block
    chunkReaderBlock(offset, chunkSize, file);
  }

  return (
    <Box spacing={Spacing.M}>
      <Headline size={HeadlineSize.L} spacingBottom={Spacing.M}>{label}</Headline>

     <Stack spacing={Spacing.L}>
        <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnPaper} headline="Instructions"
        subheadline="Note">
          <p>Use the options below to upload a CSV file containing your Contact records.</p>
        </Callout>
      
        <RadioGroup label="Select Upload Mode" name="uploadMode" 
          size={RadioGroupSize.Medium} 
          markAsRequired={true}
          value={importKind}
          onChange={setImportKind}>
            {options.map((option, key) =>
                <RadioButton key={key} {...option}>
                    {option.label}
                </RadioButton>
            )}
        </RadioGroup>
      
        <Select label="Assign to Contact Group" 
        clearable={true}
          placeholder="Select Group" onChange={setContactGroup} value={contactGroup} disabled={!contactGroups.length}>
            {contactGroups.map(c => 
              <MenuItem primaryLabel={c.displayName}
                                key={c.guid}
                                value={c.guid} />)}
        </Select>

        <Input label="CSV record delimeter" type="text" onChange={v => setDelimiter(v.target.value)} value={delimiter} />
        <Input label="Batch size" type="number" onChange={v => setDelimiter(v.target.value)} value={batchSize} min={1}  />

        <UploadTile acceptFiles=".csv" firstLineLabel="Drag&Drop .csv here" secondLineLabel="or" buttonLabel="Browse" size={UploadTileSize.Compact} onUpload={l => setFile(l[0])} />
      
      
      { currentFile.total > 0 && (
        <div>
          <Headline size={HeadlineSize.S} spacingBottom={Spacing.M}>Upload Progress</Headline>
        <ProgressBar completed={Math.floor((currentFile.current / currentFile.total) * 100)} />
        </div>
      )
      }
      
      <Button
        label="Upload file"
        size={ButtonSize.S}
        onClick={() => {
          if (!file) {
            alert('no file selected');

            return;
          }
          
          let port = location.port != '' ? `:${location.port}` : '';
          const socket = new WebSocket('wss://' + location.hostname + `${port}/contactsimport/ws`);
          socket.binaryType = 'blob';
          socket.onerror = (e) => {
            console.log('error occured', e);
            canContinue = false;
            socket.close();
          }
          socket.onmessage = (event) => {
            var p = JSON.parse(event.data);
            switch (p.type) {
              case "headerConfirmed": {
                console.log('header confirmed, starting import');
                parseFile(file, (buffer) => {
                  // console.log('chunk');     
                  if (socket.readyState == socket.OPEN) {
                    socket.send(buffer);
                    return true;
                  }
                  else {
                    return false;
                  }
                }, () => {
                  setTimeout(() => {

                    // socket.send("---FINISHED---");
                    socket.close();
                  }, 2000);
                });
                break;
              }
              case "toofast": {
                toofast = true;
                break;
              }
              case "msg": {
                setState((prev) => [
                  ...prev,
                  p.payload
                ])
                break;
              }
              case "progress": {
                const len = parseInt(p.payload);
                setCurrentFile(prev => ({
                  total: prev.total,
                  current: prev.current + len
                }))
                break;
              }
              case "finished": {
                console.log('closing socket');
                canContinue = false;
                if (socket.readyState < socket.CLOSING) {
                  socket.close();
                }
                break;
              }
            }
          };
          socket.onopen = function (event) {
            canContinue = true;
            setState((prev) => [
              ...prev,
              `Sending file of length: ${file.size}`
            ])
            console.log('connected');
            setCurrentFile({
              total: file.size,
              current: 0
            })

            socket.send(JSON.stringify({
              type: 'header',
              payload: {
                importKind,
                contactGroup: contactGroup === '<null>' ? null : contactGroup,
                delimiter,
                batchSize
              }
            }));
          };
        }}
      />
      { state.length > 0 && <pre style={{ overflow: 'scroll' }}>{[...state].reverse().join("\r\n")}</pre>}
      </Stack>
    </Box>
  );
};
