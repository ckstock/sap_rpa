const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const root=path.resolve(__dirname,'..');
const read=(...p)=>fs.readFileSync(path.join(root,...p),'utf8');
test('SAP message-server and logon-group configuration remains supported',()=>{
 const configure=read('上线安装包','scripts','configure_sap_login.ps1');
 const fetcher=read('网页启动登录','SapWebLauncher','Zfi019NlMemoryFetcher.cs');
 const program=read('网页启动登录','SapWebLauncher','Program.cs');
 for(const token of ['connectionMode','messageServerHost','messageServerService','systemId','logonGroup'])assert.match(configure,new RegExp(token,'i'),'configuration field missing: '+token);
 for(const token of ['RfcConfigParameters.MessageServerHost','RfcConfigParameters.MessageServerService','RfcConfigParameters.SystemID','RfcConfigParameters.LogonGroup'])assert.ok(fetcher.includes(token),'NCo mapping missing: '+token);
 assert.match(program,/sapshcut/i);
 assert.match(program,/(mshost|messageServerHost)/i);
 assert.match(program,/(group|logonGroup)/i);
});
