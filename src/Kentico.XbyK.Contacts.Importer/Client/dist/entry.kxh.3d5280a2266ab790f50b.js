System.register(["react","@kentico/xperience-admin-components"],(function(e,t){var r={},o={};return{setters:[function(e){r.default=e.default,r.useState=e.useState},function(e){o.Button=e.Button,o.ButtonSize=e.ButtonSize}],execute:function(){e((()=>{var e={722:(e,t,r)=>{const o=r(905).R;t.s=function(e){if(e||(e=1),!r.y.meta||!r.y.meta.url)throw console.error("__system_context__",r.y),Error("systemjs-webpack-interop was provided an unknown SystemJS context. Expected context.meta.url, but none was provided");r.p=o(r.y.meta.url,e)}},905:(e,t,r)=>{t.R=function(e,t){var r=document.createElement("a");r.href=e;for(var o="/"===r.pathname[0]?r.pathname:"/"+r.pathname,n=0,l=o.length;n!==t&&l>=0;)"/"===o[--l]&&n++;if(n!==t)throw Error("systemjs-webpack-interop: rootDirectoryLevel ("+t+") is greater than the number of directories ("+n+") in the URL path "+e);var a=o.slice(0,l+1);return r.protocol+"//"+r.host+a};Number.isInteger},271:e=>{"use strict";e.exports=o},954:e=>{"use strict";e.exports=r}},n={};function l(t){var r=n[t];if(void 0!==r)return r.exports;var o=n[t]={exports:{}};return e[t](o,o.exports,l),o.exports}l.y=t,l.d=(e,t)=>{for(var r in t)l.o(t,r)&&!l.o(e,r)&&Object.defineProperty(e,r,{enumerable:!0,get:t[r]})},l.o=(e,t)=>Object.prototype.hasOwnProperty.call(e,t),l.r=e=>{"undefined"!=typeof Symbol&&Symbol.toStringTag&&Object.defineProperty(e,Symbol.toStringTag,{value:"Module"}),Object.defineProperty(e,"__esModule",{value:!0})},l.p="";var a={};return(0,l(722).s)(1),(()=>{"use strict";l.r(a),l.d(a,{CustomLayoutTemplate:()=>r});var e=l(954),t=l(271);const r=r=>{let{label:o}=r;const[n,l]=(0,e.useState)(o),[a,s]=(0,e.useState)(null),[u,c]=(0,e.useState)([]),[i,d]=(0,e.useState)({current:0,total:0});return e.default.createElement("div",null,e.default.createElement("h1",null,n),e.default.createElement("input",{type:"file",onChange:e=>null!=e.target.files?s(e.target.files[0]||null):null}),e.default.createElement("pre",{style:{border:"2px dotted magenta"}},`Progress:${Math.floor(i.current/i.total*100)}%\nBytesSent: ${i.current}\nTotal: ${i.total}      \n      `),e.default.createElement(t.Button,{label:"Send file",size:t.ButtonSize.S,onClick:()=>{if(null!=a){console.log(a);let e=""!=location.port?`:${location.port}`:"";const t=new WebSocket("wss://"+location.hostname+`${e}/contactsimport/ws`);t.binaryType="blob",t.onmessage=e=>{var r=JSON.parse(e.data);switch(r.type){case"msg":c((e=>[...e,r.payload]));break;case"progress":{const e=parseInt(r.payload);d((t=>({total:t.total,current:t.current+e})));break}case"finished":console.log("closing socket"),t.readyState<t.CLOSING&&t.close()}},t.onopen=function(e){c((e=>[...e,`Sending file of length: ${a.size}`])),console.log("connected"),d({total:a.size,current:0}),function(e,r,o){var n=e.size,l=0,a=null,s=function(r){if(null!=r.target){if(null!=r.target.error||null==r.target.result)return o(),void console.log("Read error: "+r.target.error);if(l+=r.target.result.length,s=r.target.result,t.send(s),l>=n)return o(),void console.log("Done reading file");null!=a&&a(l,8192,e)}else console.log("progress is null");var s};(a=function(e,t,r){var o=new FileReader,n=r.slice(e,t+e);o.onload=s,o.readAsText(n)})(l,8192,e)}(a,0,(()=>{setTimeout((()=>{t.dispatchEvent,t.send("---FINISHED---")}),2e3)}))}}else alert("no file selected")}}),e.default.createElement("pre",{style:{overflow:"scroll"}},[...u].reverse().join("\r\n")))}})(),a})())}}}));