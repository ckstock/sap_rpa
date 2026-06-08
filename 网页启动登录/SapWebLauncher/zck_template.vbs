' ZCK 执行模板 — 由 SapWebLauncher 动态注入参数
' 占位符 {OK_CODE} {FIELD1_NAME} {FIELD1_VALUE} {BUTTON_ID}

On Error Resume Next

Dim SapGuiAuto, application, connection, session
Dim retries, maxRetries, sleepMs

maxRetries = 100

For retries = 1 To maxRetries
   Err.Clear
   Set SapGuiAuto = GetObject("SAPGUI")
   If Err.Number = 0 Then
      Set application = SapGuiAuto.GetScriptingEngine
      If Err.Number = 0 And IsObject(application) Then
         If application.Children.Count > 0 Then
            Set connection = application.Children(0)
            If Err.Number = 0 And IsObject(connection) Then
               If connection.Children.Count > 0 Then
                  Set session = connection.Children(0)
                  If Err.Number = 0 And IsObject(session) Then
                     WScript.Echo "INFO: session ready"
                     Exit For
                  End If
               End If
            End If
         End If
      End If
   End If

   If retries = 1 Or retries Mod 25 = 0 Then
      WScript.Echo "INFO: waiting sap session, retry=" & retries
   End If

   If retries <= 50 Then
      sleepMs = 100
   ElseIf retries <= 80 Then
      sleepMs = 200
   Else
      sleepMs = 500
   End If

   WScript.Sleep sleepMs
Next

If Not IsObject(session) Then
   WScript.Echo "ERROR: SAP GUI session not ready after adaptive wait"
   WScript.Quit 2
End If

Err.Clear
WScript.Echo "INFO: transaction already opened by sapshcut"

Err.Clear
WScript.Echo "INFO: filling field {FIELD1_NAME}"
session.findById("wnd[0]/usr/{FIELD1_NAME}").text = "{FIELD1_VALUE}"
session.findById("wnd[0]/usr/{FIELD1_NAME}").caretPosition = {CARET_POS}
If Err.Number <> 0 Then
   Err.Clear
   WScript.Echo "INFO: field not found, fallback to manual tcode {OK_CODE}"
   session.findById("wnd[0]/tbar[0]/okcd").text = "{OK_CODE}"
   session.findById("wnd[0]").sendVKey 0
   If Err.Number <> 0 Then
      WScript.Echo "ERROR: fallback tcode failed - " & Err.Description
      WScript.Quit 4
   End If

   Err.Clear
   WScript.Echo "INFO: refilling field {FIELD1_NAME}"
   session.findById("wnd[0]/usr/{FIELD1_NAME}").text = "{FIELD1_VALUE}"
   session.findById("wnd[0]/usr/{FIELD1_NAME}").caretPosition = {CARET_POS}
   If Err.Number <> 0 Then
      WScript.Echo "ERROR: fill field failed - " & Err.Description
      WScript.Quit 4
   End If
End If

Err.Clear
WScript.Echo "INFO: pressing execute button {BUTTON_ID}"
session.findById("wnd[0]/tbar[1]/btn[{BUTTON_ID}]").press
If Err.Number <> 0 Then
   WScript.Echo "ERROR: press execute failed - " & Err.Description
   WScript.Quit 5
End If

WScript.Echo "INFO: ZCK script executed"
