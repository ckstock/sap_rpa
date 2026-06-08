' Generic SAP transaction template - parameters injected by SapWebLauncher
' Placeholders: {OK_CODE} {SCRIPT_MODE} {FIELD1_NAME} {FIELD1_VALUE} {FIELD2_NAME} {FIELD2_VALUE} {PLANTS} {BUSINESS_AREAS} {FACTORY_GROUP} {RUN_STRATEGY} {PERIOD} {WEEK_END} {CARET_POS} {BUTTON_ID}

On Error Resume Next

Dim SapGuiAuto, application, connection, session
Dim retries, maxRetries, sleepMs
Dim tcode, scriptMode, field1Name, field1Value, field2Name, field2Value, buttonId
Dim plantsCsv, businessAreasCsv, factoryGroup, runStrategy, periodValue, weekEndValue

tcode = "{OK_CODE}"
scriptMode = LCase("{SCRIPT_MODE}")
field1Name = "{FIELD1_NAME}"
field1Value = "{FIELD1_VALUE}"
field2Name = "{FIELD2_NAME}"
field2Value = "{FIELD2_VALUE}"
plantsCsv = "{PLANTS}"
businessAreasCsv = "{BUSINESS_AREAS}"
factoryGroup = "{FACTORY_GROUP}"
runStrategy = "{RUN_STRATEGY}"
periodValue = "{PERIOD}"
weekEndValue = "{WEEK_END}"
buttonId = "{BUTTON_ID}"
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

If plantsCsv <> "" Then WScript.Echo "INFO: plants=" & plantsCsv
If businessAreasCsv <> "" Then WScript.Echo "INFO: businessAreas=" & businessAreasCsv
If factoryGroup <> "" Then WScript.Echo "INFO: factoryGroup=" & factoryGroup
If runStrategy <> "" Then WScript.Echo "INFO: runStrategy=" & runStrategy
If periodValue <> "" Then WScript.Echo "INFO: period=" & periodValue
If weekEndValue <> "" Then WScript.Echo "INFO: weekEnd=" & weekEndValue

Err.Clear
WScript.Echo "INFO: ensuring transaction " & tcode
session.findById("wnd[0]/tbar[0]/okcd").text = "/n" & tcode
session.findById("wnd[0]").sendVKey 0
If Err.Number <> 0 Then
   WScript.Echo "ERROR: open transaction failed - " & Err.Description
   WScript.Quit 3
End If

If scriptMode = "openonly" Or scriptMode = "" Then
   WScript.Echo "INFO: openOnly mode, transaction opened"
   WScript.Echo "INFO: transaction script executed"
   WScript.Quit 0
End If

If field1Name <> "" Then
   Err.Clear
   WScript.Echo "INFO: filling field " & field1Name
   session.findById("wnd[0]/usr/" & field1Name).text = field1Value
   session.findById("wnd[0]/usr/" & field1Name).caretPosition = {CARET_POS}
   If Err.Number <> 0 Then
      WScript.Echo "ERROR: fill field1 failed - " & Err.Description
      WScript.Quit 4
   End If
End If

If field2Name <> "" Then
   Err.Clear
   WScript.Echo "INFO: filling field " & field2Name
   session.findById("wnd[0]/usr/" & field2Name).text = field2Value
   If Err.Number <> 0 Then
      WScript.Echo "ERROR: fill field2 failed - " & Err.Description
      WScript.Quit 4
   End If
End If

If buttonId <> "" Then
   Err.Clear
   WScript.Echo "INFO: pressing execute button " & buttonId
   session.findById("wnd[0]/tbar[1]/btn[" & buttonId & "]").press
   If Err.Number <> 0 Then
      WScript.Echo "ERROR: press execute failed - " & Err.Description
      WScript.Quit 5
   End If
Else
   WScript.Echo "INFO: no button configured, transaction left open"
End If

WScript.Echo "INFO: transaction script executed"
