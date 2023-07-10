import React, { useState } from "react";
import { Button, ButtonSize, FileInput, UploadTile, UploadTileSize } from "@kentico/xperience-admin-components";


/*
* This file demonstrates a custom UI page template.
  The template supports a single page command that retrieves a string value from the backend.

  In this example, the command retrieves the server's DateTime.Now value and displays it in a label.
  See ~\UIPages\CustomTemplate\CustomTemplate.cs for the backend definition of the page.
*/

interface CustomLayoutProps {
  readonly label: string;
}

export const CustomLayoutTemplate = ({ label }: CustomLayoutProps) => {
  const [labelValue, setLabelValue] = useState(label);

  const [file, setFile] = useState<File | null>(null);
  const [state, setState] = useState<string[]>([]);
  const [currentFile, setCurrentFile] = useState({ current: 0, total: 0 });

  function parseFile(this: any, file: File, callback: (buffer: ArrayBuffer) => void, finishedCallback: () => void) {
    var fileSize = file.size;
    var chunkSize = 8 * 1024; // bytes
    var offset = 0;
    var self = this as unknown; // we need a reference to the current object
    var chunkReaderBlock: null | ((_offset: number, length: number, _file: File) => void) = null;

    var readEventHandler = function (evt: ProgressEvent<FileReader>) {
      if (evt.target == null) {
        console.log('progress is null');
        return;
      }
      if (evt.target.error == null && evt.target.result != null) {
        offset += (evt.target.result as any).length;
        callback(evt.target.result as ArrayBuffer); // callback for handling read chunk
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
      if (chunkReaderBlock != null) chunkReaderBlock(offset, chunkSize, file);
    }

    chunkReaderBlock = function (_offset: number, length: number, _file: File) {
      var r = new FileReader();
      var blob = _file.slice(_offset, length + _offset);
      r.onload = readEventHandler;
      r.readAsText(blob);
    }

    // now let's start the read with the first block
    chunkReaderBlock(offset, chunkSize, file);
  }

  return (
    <div>
      <h1>{labelValue}</h1>
      {/* {fu} */}
      <input type="file" onChange={(event) => event.target.files != null ? setFile(event.target.files[0] || null) : null} />
      <pre style={{ border: '2px dotted magenta' }}>{`Progress:${Math.floor((currentFile.current / currentFile.total) * 100)}%
BytesSent: ${currentFile.current}
Total: ${currentFile.total}      
      `}</pre>
      <Button
        label="Send file"
        size={ButtonSize.S}
        onClick={() => {
          if (file != null) {
            console.log(file);
            let port = location.port != '' ? `:${location.port}` : '';
            const socket = new WebSocket('wss://' + location.hostname + `${port}/contactsimport/ws`);
            socket.binaryType = 'blob';
            socket.onmessage = (event) => {
              var p = JSON.parse(event.data);
              switch (p.type) {
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
                  if(socket.readyState < socket.CLOSING){
                    socket.close();
                  }
                  break;
                }
              }
            };
            socket.onopen = function (event) {
              setState((prev) => [
                ...prev,
                `Sending file of length: ${file.size}`
              ])
              console.log('connected');
              setCurrentFile({
                total: file.size,
                current: 0
              })

              parseFile(file, (buffer) => {
                // console.log('chunk');
                socket.send(buffer);
              }, () => {
                setTimeout(() => {                
                  socket.dispatchEvent                  
                  socket.send("---FINISHED---");
                }, 2000);
              });
            };
          } else {
            alert('no file selected');
          }
        }}
      />
      <pre style={{overflow: 'scroll'}}>{[...state].reverse().join("\r\n")}</pre>
    </div>
  );
};
