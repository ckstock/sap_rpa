' Generic SAP transaction template - parameters injected by SapWebLauncher
' Placeholders: {OK_CODE} {SCRIPT_MODE} {FIELD1_NAME} {FIELD1_VALUE} {FIELD2_NAME} {FIELD2_VALUE} {PLANTS} {BUSINESS_AREAS} {FACTORY_GROUP} {RUN_STRATEGY} {YEAR} {WEEK} {PERIOD} {WEEK_END} {CARET_POS} {BUTTON_ID}

On Error Resume Next

Dim SapGuiAuto, application, connection, session
Dim retries, maxRetries, sleepMs
Dim tcode, scriptMode, field1Name, field1Value, field2Name, field2Value, buttonId
Dim plantsCsv, businessAreasCsv, factoryGroup, runStrategy, periodValue, weekEndValue
Dim yearValue, weekValue
Dim isLoginScreen, okCodeReady, windowTitle, statusType, statusText, sessionUser

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
yearValue = "{YEAR}"
weekValue = "{WEEK}"
weekEndValue = "{WEEK_END}"
buttonId = "{BUTTON_ID}"
maxRetries = 100

Function ObjectExists(id)
   Dim obj
   Err.Clear
   Set obj = session.findById(id)
   ObjectExists = (Err.Number = 0 And IsObject(obj))
   Err.Clear
End Function

Function SafeText(id)
   Dim obj
   Err.Clear
   Set obj = session.findById(id)
   If Err.Number = 0 And IsObject(obj) Then
      SafeText = obj.Text
   Else
      SafeText = ""
   End If
   Err.Clear
End Function

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
                     isLoginScreen = ObjectExists("wnd[0]/usr/pwdRSYST-BCODE")
                     okCodeReady = ObjectExists("wnd[0]/tbar[0]/okcd")
                     windowTitle = SafeText("wnd[0]")
                     statusText = SafeText("wnd[0]/sbar")
                     Err.Clear
                     sessionUser = session.Info.User
                     If Err.Number <> 0 Then sessionUser = ""
                     Err.Clear

                     If (Not isLoginScreen) And okCodeReady Then
                        WScript.Echo "INFO: session ready, user=" & sessionUser & ", title=" & windowTitle
                        Exit For
                     End If

                     If retries = 1 Or retries Mod 10 = 0 Then
                        If isLoginScreen Then
                           WScript.Echo "INFO: waiting SAP login to finish, title=" & windowTitle & ", status=" & statusText
                        Else
                           WScript.Echo "INFO: waiting SAP command field, title=" & windowTitle & ", status=" & statusText
                        End If
                     End If
                  End If
               End If
            End If
         End If
      End If
   End If

   If (Not IsObject(session)) And (retries = 1 Or retries Mod 25 = 0) Then
      WScript.Echo "INFO: waiting sap session, retry=" & retries
   End If

   If retries <= 40 Then
      sleepMs = 250
   ElseIf retries <= 80 Then
      sleepMs = 500
   Else
      sleepMs = 1000
   End If

   WScript.Sleep sleepMs
Next

If Not IsObject(session) Then
   WScript.Echo "ERROR: SAP GUI session not ready after adaptive wait"
   WScript.Quit 2
End If

If ObjectExists("wnd[0]/usr/pwdRSYST-BCODE") Then
   WScript.Echo "ERROR: SAP login screen is still active. Check local config user/password/system/client."
   WScript.Quit 7
End If

If Not ObjectExists("wnd[0]/tbar[0]/okcd") Then
   WScript.Echo "ERROR: SAP command field is not ready. Current title=" & SafeText("wnd[0]") & ", status=" & SafeText("wnd[0]/sbar")
   WScript.Quit 8
End If

If plantsCsv <> "" Then WScript.Echo "INFO: plants=" & plantsCsv
If businessAreasCsv <> "" Then WScript.Echo "INFO: businessAreas=" & businessAreasCsv
If factoryGroup <> "" Then WScript.Echo "INFO: factoryGroup=" & factoryGroup
If runStrategy <> "" Then WScript.Echo "INFO: runStrategy=" & runStrategy
If yearValue <> "" Then WScript.Echo "INFO: year=" & yearValue
If weekValue <> "" Then WScript.Echo "INFO: week=" & weekValue
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
WScript.Sleep 1000
Err.Clear
statusType = session.findById("wnd[0]/sbar").MessageType
statusText = session.findById("wnd[0]/sbar").Text
If Err.Number = 0 And statusText <> "" Then
   WScript.Echo "INFO: sap status type=" & statusType & ", text=" & statusText
End If
If Err.Number = 0 And (statusType = "E" Or statusType = "A") Then
   WScript.Echo "ERROR: SAP rejected transaction " & tcode & " - " & statusText
   WScript.Quit 6
End If
Err.Clear

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
