' @tcode=ZFIR034
' @name=ZFIR034 date range report
' @params=period,weekEnd
' @dateRule=LAST_FULL_WEEK_BY_SYSTEM_DATE
' @factoryRule=system previous full week only; no plant or business area input
'
' Standardized for SapWebLauncher. Source is ASCII/WSH safe.

On Error Resume Next

Dim tcode, factoryGroup
Dim yearValue, weekValue, periodValue, weekEndValue, dateLowValue, dateHighValue
Dim SapGuiAuto, application, connection, session
Dim retries, sleepMs, statusType, statusText
Dim unresolvedOkCodeToken, unresolvedAlvExportDirToken, unresolvedAlvExportFilenameToken
Dim alvExportDir, alvExportFilename, scriptDir, exportTimeoutMs, alvHelperLoaded

tcode = "{OK_CODE}"
factoryGroup = "{FACTORY_GROUP}"
yearValue = "{YEAR}"
weekValue = "{WEEK}"
periodValue = "{PERIOD}"
weekEndValue = "{WEEK_END}"
alvExportDir = "{ALV_EXPORT_DIR}"
alvExportFilename = "{ALV_EXPORT_FILENAME}"
scriptDir = "{SCRIPT_DIR}"
exportTimeoutMs = 180000
alvHelperLoaded = False
unresolvedOkCodeToken = "{" & "OK_CODE" & "}"
unresolvedAlvExportDirToken = "{" & "ALV_EXPORT_DIR" & "}"
unresolvedAlvExportFilenameToken = "{" & "ALV_EXPORT_FILENAME" & "}"

If Trim(CStr(tcode)) = "" Or Trim(CStr(tcode)) = unresolvedOkCodeToken Then tcode = "ZFIR034"
If UCase(Trim(CStr(tcode))) <> "ZFIR034" Then Fail "ZFIR034 script refuses tcode=" & CStr(tcode), 10
If IsPlaceholder(yearValue, "YEAR") Then yearValue = ""
If IsPlaceholder(weekValue, "WEEK") Then weekValue = ""
If IsPlaceholder(periodValue, "PERIOD") Then periodValue = ""
If IsPlaceholder(weekEndValue, "WEEK_END") Then weekEndValue = ""
If Trim(CStr(alvExportDir)) = unresolvedAlvExportDirToken Then alvExportDir = ""
If Trim(CStr(alvExportFilename)) = unresolvedAlvExportFilenameToken Then alvExportFilename = ""
If IsPlaceholder(scriptDir, "SCRIPT_DIR") Then scriptDir = ""

ResolveDates

Function IsPlaceholder(value, tokenName)
   IsPlaceholder = (Trim(CStr(value)) = "{" & tokenName & "}")
End Function

Function FormatSapDate(value)
   FormatSapDate = Year(value) & "." & Right("0" & Month(value), 2) & "." & Right("0" & Day(value), 2)
End Function

Function WeekStart(d)
   WeekStart = DateAdd("d", 1 - Weekday(d, vbMonday), d)
End Function

Function ParseDateOrEmpty(value)
   Dim v, parts
   ParseDateOrEmpty = Empty
   v = Replace(Trim(CStr(value)), ".", "-")
   v = Replace(v, "/", "-")
   parts = Split(v, "-")
   If UBound(parts) = 2 Then
      If IsNumeric(parts(0)) And IsNumeric(parts(1)) And IsNumeric(parts(2)) Then
         ParseDateOrEmpty = DateSerial(CInt(parts(0)), CInt(parts(1)), CInt(parts(2)))
      End If
   End If
End Function

Function CombinePath(folderPath, fileName)
   If Right(CStr(folderPath), 1) = "\" Then
      CombinePath = CStr(folderPath) & CStr(fileName)
   Else
      CombinePath = CStr(folderPath) & "\" & CStr(fileName)
   End If
End Function

Function LoadAlvExportHelper()
   Dim fso, helperPath, textFile, helperText
   On Error Resume Next
   LoadAlvExportHelper = False
   If alvHelperLoaded Then
      LoadAlvExportHelper = True
      Exit Function
   End If
   Set fso = CreateObject("Scripting.FileSystemObject")
   If Trim(CStr(scriptDir)) = "" Then scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
   helperPath = CombinePath(scriptDir, "sap_alv_export_helper.vbs")
   If Not fso.FileExists(helperPath) Then Fail "ALV export helper not found: " & helperPath, 8
   Set textFile = fso.OpenTextFile(helperPath, 1, False, -2)
   If Err.Number <> 0 Then Fail "open ALV export helper failed - " & Err.Description, 8
   helperText = textFile.ReadAll
   textFile.Close
   ExecuteGlobal helperText
   If Err.Number <> 0 Then Fail "load ALV export helper failed - " & Err.Description, 8
   alvHelperLoaded = True
   LoadAlvExportHelper = True
   Err.Clear
End Function

Sub ExportAlvResult(label)
   If Not LoadAlvExportHelper() Then Fail "ALV export helper could not be loaded", 8
   If Not AlvExportIfConfigured(session, alvExportDir, alvExportFilename, exportTimeoutMs) Then Fail "ALV export returned false after " & label, 8
   WScript.Echo "INFO: ALV export completed after " & label
End Sub

Sub ResolveDates()
   Dim parsedEnd, parsedStart, defaultEnd, defaultStart
   defaultStart = DateAdd("d", -7, WeekStart(Date))
   defaultEnd = DateAdd("d", 6, defaultStart)
   parsedStart = ParseDateOrEmpty(periodValue)
   parsedEnd = ParseDateOrEmpty(weekEndValue)
   If IsEmpty(parsedStart) Then parsedStart = defaultStart
   If IsEmpty(parsedEnd) Then parsedEnd = defaultEnd
   yearValue = Year(parsedStart)
   If Trim(CStr(weekValue)) = "" Then weekValue = DatePart("ww", parsedStart, vbMonday, vbFirstFourDays)
   dateLowValue = FormatSapDate(parsedStart)
   dateHighValue = FormatSapDate(parsedEnd)
End Sub

Sub Fail(message, code)
   WScript.Echo "STATUS_TYPE=E"
   WScript.Echo "STATUS_TEXT=" & message
   WScript.Echo "ERROR=" & message
   WScript.Echo "ERROR: " & message
   WScript.Quit code
End Sub

Function ObjectExists(id)
   Dim obj
   Err.Clear
   Set obj = session.findById(id)
   ObjectExists = (Err.Number = 0 And IsObject(obj))
   Err.Clear
End Function

Function SessionIsUsable(candidate)
   SessionIsUsable = False
   If Not IsObject(candidate) Then Exit Function
   Err.Clear
   If candidate.Info.User = "" Then Err.Clear: Exit Function
   If candidate.Info.Transaction = "S000" Then Err.Clear: Exit Function
   If Err.Number <> 0 Then Err.Clear: Exit Function
   Set session = candidate
   If ObjectExists("wnd[0]/tbar[0]/okcd") Then SessionIsUsable = True
   Err.Clear
End Function

Sub WaitReady(timeoutMs)
   Dim waited
   waited = 0
   Do While waited <= timeoutMs
      Err.Clear
      If Not CBool(session.Busy) Then Err.Clear: Exit Sub
      Err.Clear
      WScript.Sleep 250
      waited = waited + 250
   Loop
   WScript.Echo "WARN: SAP session still busy after wait"
End Sub

Sub CheckSapStatus(stage)
   Err.Clear
   statusType = session.findById("wnd[0]/sbar").MessageType
   statusText = session.findById("wnd[0]/sbar").Text
   If Err.Number = 0 And Trim(CStr(statusText)) <> "" Then WScript.Echo "INFO: sap status after " & stage & " type=" & statusType & ", text=" & statusText
   If Err.Number = 0 And (statusType = "E" Or statusType = "A") Then Fail "SAP status error after " & stage & " - " & statusText, 6
   Err.Clear
End Sub

Sub SetField(label, id, value)
   Err.Clear
   session.findById(id).Text = CStr(value)
   If Err.Number <> 0 Then Fail "set " & label & " failed - " & Err.Description, 9
   WScript.Echo "INFO: set " & label & "=" & CStr(value)
   Err.Clear
End Sub

Function SetFieldByCandidates(label, ids, value)
   Dim id, obj
   SetFieldByCandidates = False
   For Each id In ids
      Err.Clear
      Set obj = session.findById(CStr(id))
      If Err.Number = 0 And IsObject(obj) Then
         obj.Text = CStr(value)
         If Err.Number = 0 Then
            WScript.Echo "INFO: set " & label & "=" & CStr(value) & " via " & CStr(id)
            SetFieldByCandidates = True
            Err.Clear
            Exit Function
         End If
      End If
      Err.Clear
   Next
   Fail "set " & label & " failed - SAP field not found", 9
End Function

Function FocusFieldByCandidates(ids, caretValue)
   Dim id, obj
   FocusFieldByCandidates = False
   For Each id In ids
      Err.Clear
      Set obj = session.findById(CStr(id))
      If Err.Number = 0 And IsObject(obj) Then
         obj.SetFocus
         obj.caretPosition = Len(CStr(caretValue))
         If Err.Number = 0 Then
            FocusFieldByCandidates = True
            Err.Clear
            Exit Function
         End If
      End If
      Err.Clear
   Next
End Function

Sub PressExecute()
   Err.Clear
   session.findById("wnd[0]/tbar[1]/btn[8]").press
   If Err.Number <> 0 Then Fail "execute failed - " & Err.Description, 8
   WScript.Echo "INFO: pressed execute"
   Err.Clear
   WaitReady 600000
   CheckSapStatus "execute"
End Sub

For retries = 1 To 100
   Err.Clear
   Set SapGuiAuto = GetObject("SAPGUI")
   If Err.Number = 0 Then
      Set application = SapGuiAuto.GetScriptingEngine
      If Err.Number = 0 And IsObject(application) And application.Children.Count > 0 Then
         Dim connIndex, sessIndex, candidateConnection, candidateSession
         For connIndex = 0 To application.Children.Count - 1
            Set candidateConnection = application.Children.Item(CInt(connIndex))
            If Err.Number = 0 And IsObject(candidateConnection) And candidateConnection.Children.Count > 0 Then
               For sessIndex = 0 To candidateConnection.Children.Count - 1
                  Set candidateSession = candidateConnection.Children.Item(CInt(sessIndex))
                  If Err.Number = 0 And SessionIsUsable(candidateSession) Then Exit For
                  Err.Clear
               Next
            End If
            If IsObject(session) And SessionIsUsable(session) Then Exit For
            Err.Clear
         Next
         If IsObject(session) And SessionIsUsable(session) Then Exit For
      End If
   End If
   Err.Clear
   If retries <= 40 Then
      sleepMs = 250
   ElseIf retries <= 80 Then
      sleepMs = 500
   Else
      sleepMs = 1000
   End If
   WScript.Sleep sleepMs
Next

If Not IsObject(session) Or Not SessionIsUsable(session) Then Fail "logged-in SAP GUI session not ready after adaptive wait", 2
If Not ObjectExists("wnd[0]/tbar[0]/okcd") Then Fail "SAP command field is not ready", 7

WScript.Echo "INFO: transaction=" & tcode
WScript.Echo "INFO: year=" & yearValue
WScript.Echo "INFO: week=" & weekValue
WScript.Echo "INFO: period=" & dateLowValue
WScript.Echo "INFO: weekEnd=" & dateHighValue
WScript.Echo "INFO: date input group #1; range=[" & dateLowValue & "~" & dateHighValue & "]; P_GJAHR=" & yearValue & "; P_WEEK=" & weekValue
If factoryGroup <> "" And Not IsPlaceholder(factoryGroup, "FACTORY_GROUP") Then WScript.Echo "INFO: factoryGroup=" & factoryGroup

Err.Clear
session.findById("wnd[0]").maximize
session.findById("wnd[0]/tbar[0]/okcd").Text = "/n" & tcode
session.findById("wnd[0]").sendVKey 0
If Err.Number <> 0 Then Fail "open transaction failed - " & Err.Description, 3
Err.Clear
WaitReady 8000
CheckSapStatus "open transaction"

' === SAP operation block ===
SetFieldByCandidates "p-gjahr", Array("wnd[0]/usr/txtP_GJAHR", "wnd[0]/usr/ctxtP_GJAHR"), yearValue
SetFieldByCandidates "p-week", Array("wnd[0]/usr/txtP_WEEK", "wnd[0]/usr/ctxtP_WEEK"), weekValue
SetField "budat-low", "wnd[0]/usr/ctxtS_BUDAT-LOW", dateLowValue
SetField "budat-high", "wnd[0]/usr/ctxtS_BUDAT-HIGH", dateHighValue
Err.Clear
FocusFieldByCandidates Array("wnd[0]/usr/ctxtS_BUDAT-HIGH"), dateHighValue
Err.Clear
PressExecute
ExportAlvResult "execute"

CheckSapStatus "finish"
WScript.Echo "INFO: transaction script executed"
WScript.Quit 0
