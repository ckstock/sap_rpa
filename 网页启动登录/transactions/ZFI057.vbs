' @tcode=ZFI057
' @name=ZFI057 value split
' @params=plants,businessAreas,period,weekEnd
' @dateRule=RELEASE_COST_MONTH_WITH_CROSS_MONTH_SPLIT
' @factoryRule=business area maps to one or more plants through ZFIT_RPA_BUKRS GSBER/WERKS; backend invokes this script once per mapped plant
'
' Standardized for SapWebLauncher. Source is ASCII/WSH safe.

On Error Resume Next

Dim tcode, plantsCsv, businessAreasCsv, factoryGroup, materialsCsv
Dim field1Name, field1Value, field2Name, field2Value
Dim yearValue, weekValue, periodValue, weekEndValue
Dim businessAreaValue, plantText, plantSeedValue, plantCount, materialText, materialCount
Dim SapGuiAuto, application, connection, session
Dim retries, sleepMs, statusType, statusText
Dim runCount, i
Dim zfi057WindowSuccessCount, zfi057WindowNoDataCount
Dim kadkyLow(3), kadkyHigh(3), kadatLow(3), kadatHigh(3)
Dim alvExportDir, alvExportFilename, scriptDir, exportTimeoutMs, alvHelperLoaded
Dim unresolvedAlvExportDirToken, unresolvedAlvExportFilenameToken

tcode = "{OK_CODE}"
plantsCsv = "{PLANTS}"
businessAreasCsv = "{BUSINESS_AREAS}"
factoryGroup = "{FACTORY_GROUP}"
materialsCsv = "{MATERIALS}"
yearValue = "{YEAR}"
weekValue = "{WEEK}"
periodValue = "{PERIOD}"
weekEndValue = "{WEEK_END}"
field1Name = "{FIELD1_NAME}"
field1Value = "{FIELD1_VALUE}"
field2Name = "{FIELD2_NAME}"
field2Value = "{FIELD2_VALUE}"
alvExportDir = "{ALV_EXPORT_DIR}"
alvExportFilename = "{ALV_EXPORT_FILENAME}"
scriptDir = "{SCRIPT_DIR}"
exportTimeoutMs = 180000
alvHelperLoaded = False
unresolvedAlvExportDirToken = "{" & "ALV_EXPORT_DIR" & "}"
unresolvedAlvExportFilenameToken = "{" & "ALV_EXPORT_FILENAME" & "}"

If IsPlaceholder(tcode, "OK_CODE") Or Trim(CStr(tcode)) = "" Then tcode = "ZFI057"
If UCase(Trim(CStr(tcode))) <> "ZFI057" Then Fail "ZFI057 script refuses tcode=" & CStr(tcode), 10
If IsPlaceholder(plantsCsv, "PLANTS") Then plantsCsv = ""
If IsPlaceholder(businessAreasCsv, "BUSINESS_AREAS") Then businessAreasCsv = ""
If IsPlaceholder(factoryGroup, "FACTORY_GROUP") Then factoryGroup = ""
If IsPlaceholder(materialsCsv, "MATERIALS") Then materialsCsv = ""
If IsPlaceholder(yearValue, "YEAR") Then yearValue = ""
If IsPlaceholder(weekValue, "WEEK") Then weekValue = ""
If IsPlaceholder(periodValue, "PERIOD") Then periodValue = ""
If IsPlaceholder(weekEndValue, "WEEK_END") Then weekEndValue = ""
If IsPlaceholder(field1Name, "FIELD1_NAME") Then field1Name = ""
If IsPlaceholder(field1Value, "FIELD1_VALUE") Then field1Value = ""
If IsPlaceholder(field2Name, "FIELD2_NAME") Then field2Name = ""
If IsPlaceholder(field2Value, "FIELD2_VALUE") Then field2Value = ""
If Trim(CStr(alvExportDir)) = unresolvedAlvExportDirToken Then alvExportDir = ""
If Trim(CStr(alvExportFilename)) = unresolvedAlvExportFilenameToken Then alvExportFilename = ""
If IsPlaceholder(scriptDir, "SCRIPT_DIR") Then scriptDir = ""

plantText = NormalizeListText(plantsCsv)
plantSeedValue = FirstCsvValue(plantsCsv)
plantCount = CountLines(plantText)
businessAreaValue = FirstCsvValue(businessAreasCsv)
If plantCount <> 1 Then Fail "ZFI057 requires exactly one plant from the backend ZFIT_RPA_BUKRS GSBER/WERKS mapping in {PLANTS}; multi-plant input must be split by the backend.", 5

materialText = ResolveMaterialText()
materialCount = CountLines(materialText)
If materialCount <= 0 Then
   Fail "ZFI057 requires material list supplied by the ZFI057 workflow after upstream ZFI019NL/ZFI_SPLIT collection.", 5
End If

ResolveZfi057DateWindows

Function IsPlaceholder(value, tokenName)
   IsPlaceholder = (Trim(CStr(value)) = "{" & tokenName & "}")
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

Sub ExportAlvForWindow(index)
   Dim exportFilename
   If Not LoadAlvExportHelper() Then Fail "ALV export helper could not be loaded", 8
   exportFilename = alvExportFilename
   If runCount > 1 Then exportFilename = AlvBuildExportFilename(alvExportFilename, "part" & CStr(index))
   If Not AlvExportIfConfigured(session, alvExportDir, exportFilename, exportTimeoutMs) Then Fail "ALV export returned false for ZFI057 group #" & index, 8
   WScript.Echo "INFO: ALV export completed for ZFI057 group #" & index
End Sub

Function FirstCsvValue(value)
   Dim parts, item
   value = Replace(CStr(value), ";", ",")
   value = Replace(value, "|", ",")
   parts = Split(value, ",")
   For Each item In parts
      item = Trim(CStr(item))
      If item <> "" Then
         FirstCsvValue = item
         Exit Function
      End If
   Next
   FirstCsvValue = ""
End Function

Function NormalizeListText(value)
   Dim text, normalized, parts, item, result
   text = Trim(CStr(value))
   text = Replace(text, vbCrLf, ",")
   text = Replace(text, vbCr, ",")
   text = Replace(text, vbLf, ",")
   text = Replace(text, ";", ",")
   text = Replace(text, "|", ",")
   parts = Split(text, ",")
   result = ""
   For Each item In parts
      normalized = Trim(CStr(item))
      If normalized <> "" Then
         If result <> "" Then result = result & vbCrLf
         result = result & normalized
      End If
   Next
   NormalizeListText = result
End Function

Function LoadMaterialSource(value)
   Dim source, path, fso, stream
   source = Trim(CStr(value))
   If LCase(Left(source, 6)) <> "@file:" Then
      LoadMaterialSource = source
      Exit Function
   End If

   path = Mid(source, 7)
   Err.Clear
   Set fso = CreateObject("Scripting.FileSystemObject")
   If Err.Number <> 0 Then Fail "create filesystem object for material file failed - " & Err.Description, 5
   If Not fso.FileExists(path) Then Fail "material file not found - " & path, 5

   Set stream = fso.OpenTextFile(path, 1, False, True)
   If Err.Number <> 0 Then Fail "read material file failed - " & Err.Description, 5
   LoadMaterialSource = stream.ReadAll
   stream.Close
   Err.Clear
End Function

Function ResolveMaterialText()
   Dim source
   source = materialsCsv
   If Trim(source) = "" And LooksLikeMaterialField(field1Name) Then source = field1Value
   If Trim(source) = "" And LooksLikeMaterialField(field2Name) Then source = field2Value
   If Trim(source) = "" And field1Name = "" And Trim(field1Value) <> "" Then source = field1Value
   If Trim(source) = "" And field2Name = "" And Trim(field2Value) <> "" Then source = field2Value
   source = LoadMaterialSource(source)
   ResolveMaterialText = NormalizeListText(source)
End Function

Function LooksLikeMaterialField(name)
   Dim v
   v = UCase(Trim(CStr(name)))
   LooksLikeMaterialField = (InStr(v, "MATNR") > 0 Or InStr(v, "MATERIAL") > 0 Or InStr(v, "MATERIALS") > 0)
End Function

Function CountLines(value)
   Dim text, parts
   text = Trim(CStr(value))
   If text = "" Then
      CountLines = 0
   Else
      parts = Split(text, vbCrLf)
      CountLines = UBound(parts) + 1
   End If
End Function

Function SapDate(value)
   SapDate = Replace(Trim(CStr(value)), "-", ".")
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
   parts = Split(v, "-")
   If UBound(parts) = 2 Then
      If IsNumeric(parts(0)) And IsNumeric(parts(1)) And IsNumeric(parts(2)) Then
         ParseDateOrEmpty = DateSerial(CInt(parts(0)), CInt(parts(1)), CInt(parts(2)))
      End If
   End If
End Function

Function PreviousReleaseMonthStart(value)
   Dim offsetDate
   If (Month(value) Mod 2) = 0 Then
      offsetDate = DateAdd("m", -1, value)
   Else
      offsetDate = DateAdd("m", -2, value)
   End If
   PreviousReleaseMonthStart = DateSerial(Year(offsetDate), Month(offsetDate), 1)
End Function

Sub ResolveZfi057DateWindows()
   Dim parsedStart, parsedEnd, defaultStart, defaultEnd
   Dim firstOfStartMonth, firstOfEndMonth, releaseMonthStart, currentMonthStart, currentMonthEnd
   defaultStart = DateAdd("d", -7, WeekStart(Date))
   defaultEnd = DateAdd("d", 6, defaultStart)
   parsedStart = ParseDateOrEmpty(periodValue)
   parsedEnd = ParseDateOrEmpty(weekEndValue)
   If IsEmpty(parsedStart) Then parsedStart = defaultStart
   If IsEmpty(parsedEnd) Then parsedEnd = defaultEnd

   If Trim(CStr(yearValue)) = "" Then yearValue = Year(parsedStart)
   If Trim(CStr(weekValue)) = "" Then weekValue = DatePart("ww", parsedStart, vbMonday, vbFirstFourDays)
   periodValue = FormatSapDate(parsedStart)
   weekEndValue = FormatSapDate(parsedEnd)

   firstOfStartMonth = DateSerial(Year(parsedStart), Month(parsedStart), 1)
   firstOfEndMonth = DateSerial(Year(parsedEnd), Month(parsedEnd), 1)
   If Year(parsedStart) = Year(parsedEnd) And Month(parsedStart) = Month(parsedEnd) Then
      runCount = 1
      kadkyLow(1) = FormatSapDate(firstOfStartMonth)
      kadkyHigh(1) = FormatSapDate(parsedEnd)
      kadatLow(1) = FormatSapDate(DateAdd("d", 1, firstOfStartMonth))
      kadatHigh(1) = FormatSapDate(parsedEnd)
   Else
      runCount = 0
      releaseMonthStart = PreviousReleaseMonthStart(parsedEnd)
      currentMonthStart = releaseMonthStart
      Do While currentMonthStart <= firstOfEndMonth
         runCount = runCount + 1
         currentMonthEnd = DateAdd("d", -1, DateAdd("m", 1, currentMonthStart))
         kadkyLow(runCount) = FormatSapDate(currentMonthStart)
         If Year(currentMonthStart) = Year(firstOfEndMonth) And Month(currentMonthStart) = Month(firstOfEndMonth) Then
            kadkyHigh(runCount) = FormatSapDate(parsedEnd)
            kadatHigh(runCount) = FormatSapDate(parsedEnd)
         Else
            kadkyHigh(runCount) = FormatSapDate(currentMonthEnd)
            kadatHigh(runCount) = FormatSapDate(currentMonthEnd)
         End If
         kadatLow(runCount) = FormatSapDate(DateAdd("d", 1, currentMonthStart))
         currentMonthStart = DateAdd("m", 1, currentMonthStart)
      Loop
   End If
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
   RefreshSapStatus
   If Err.Number = 0 And Trim(CStr(statusText)) <> "" Then WScript.Echo "INFO: sap status after " & stage & " type=" & statusType & ", text=" & statusText
   If Err.Number = 0 And (statusType = "E" Or statusType = "A") Then Fail "SAP status error after " & stage & " - " & statusText, 6
   Err.Clear
End Sub

Sub RefreshSapStatus()
   Err.Clear
   statusType = session.findById("wnd[0]/sbar").MessageType
   statusText = session.findById("wnd[0]/sbar").Text
   If Err.Number <> 0 Then
      statusType = ""
      statusText = ""
   End If
   Err.Clear
End Sub

Function IsNoDataStatusText(value)
   Dim compact
   compact = Replace(CStr(value), " ", "")
   compact = Replace(compact, vbTab, "")
   compact = Replace(compact, ChrW(12288), "")
   IsNoDataStatusText = (InStr(1, compact, "没有符合条件数据", vbTextCompare) > 0 Or _
                         InStr(1, compact, "沒有符合條件數據", vbTextCompare) > 0 Or _
                         InStr(1, UCase(compact), "NODATA", vbTextCompare) > 0)
End Function

Sub SetField(label, id, value)
   Err.Clear
   session.findById(id).Text = CStr(value)
   If Err.Number <> 0 Then Fail "set " & label & " failed - " & Err.Description, 9
   WScript.Echo "INFO: set " & label & "=" & CStr(value)
   Err.Clear
End Sub

Function PressExecuteZfi057Window(index)
   Err.Clear
   session.findById("wnd[0]/tbar[1]/btn[8]").press
   If Err.Number <> 0 Then Fail "execute ZFI057 group #" & index & " failed - " & Err.Description, 8
   WScript.Echo "INFO: pressed execute ZFI057 group #" & index
   Err.Clear
   WaitReady 1200000
   RefreshSapStatus
   If Trim(CStr(statusText)) <> "" Then WScript.Echo "INFO: sap status after execute ZFI057 group #" & index & " type=" & statusType & ", text=" & statusText
   If IsNoDataStatusText(statusText) Then
      WScript.Echo "WARN: execute ZFI057 group #" & index & " returned no data; continuing remaining windows"
      PressExecuteZfi057Window = False
      Exit Function
   End If
   If statusType = "E" Or statusType = "A" Then
      Fail "SAP status error after execute ZFI057 group #" & index & " - " & statusText, 6
   End If
   PressExecuteZfi057Window = True
End Function

Sub PressButton(id, label, timeoutMs)
   Err.Clear
   session.findById(id).press
   If Err.Number <> 0 Then Fail label & " failed - " & Err.Description, 8
   WScript.Echo "INFO: pressed " & label
   Err.Clear
   WaitReady timeoutMs
   CheckSapStatus label
End Sub

Sub SetClipboardText(value, expectedLineCount, label)
   Dim fso, shell, tempFolder, dataFile, scriptFile, dataStream, scriptStream
   Dim psScript, command, proc, stdout, stderr, verifiedLineCount
   Err.Clear
   Set fso = CreateObject("Scripting.FileSystemObject")
   If Err.Number <> 0 Then Fail "create filesystem object for clipboard failed - " & Err.Description, 8
   Err.Clear
   Set shell = CreateObject("WScript.Shell")
   If Err.Number <> 0 Then Fail "create shell object for clipboard failed - " & Err.Description, 8
   Err.Clear
   tempFolder = shell.ExpandEnvironmentStrings("%TEMP%")
   dataFile = fso.BuildPath(tempFolder, fso.GetTempName())
   scriptFile = fso.BuildPath(tempFolder, fso.GetTempName() & ".ps1")

   Set dataStream = fso.OpenTextFile(dataFile, 2, True, True)
   dataStream.Write CStr(value)
   dataStream.Close
   If Err.Number <> 0 Then
      CleanupClipboardTemp fso, dataFile, scriptFile
      Fail "write clipboard " & label & " temp file failed - " & Err.Description, 8
   End If
   Err.Clear

   psScript = "param([string]$Path)" & vbCrLf & _
      "Add-Type -AssemblyName System.Windows.Forms" & vbCrLf & _
      "$text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::Unicode)" & vbCrLf & _
      "[System.Windows.Forms.Clipboard]::SetText($text, [System.Windows.Forms.TextDataFormat]::UnicodeText)" & vbCrLf & _
      "Start-Sleep -Milliseconds 200" & vbCrLf & _
      "$actual = [System.Windows.Forms.Clipboard]::GetText([System.Windows.Forms.TextDataFormat]::UnicodeText)" & vbCrLf & _
      "if ($actual -ne $text) {" & vbCrLf & _
      "  [Console]::Error.WriteLine(('clipboard verify failed; expectedLength={0}; actualLength={1}' -f $text.Length, $actual.Length))" & vbCrLf & _
      "  exit 2" & vbCrLf & _
      "}" & vbCrLf & _
      "$lineCount = (($actual -split ""`r?`n"") | Where-Object { $_.Trim().Length -gt 0 }).Count" & vbCrLf & _
      "[Console]::Out.WriteLine(('clipboard verified length={0}; lines={1}' -f $text.Length, $lineCount))" & vbCrLf
   Set scriptStream = fso.OpenTextFile(scriptFile, 2, True, False)
   scriptStream.Write psScript
   scriptStream.Close
   If Err.Number <> 0 Then
      CleanupClipboardTemp fso, dataFile, scriptFile
      Fail "write clipboard helper script failed - " & Err.Description, 8
   End If
   Err.Clear

   command = "powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File " & ShellQuote(scriptFile) & " " & ShellQuote(dataFile)
   Set proc = shell.Exec(command)
   If Err.Number <> 0 Then
      CleanupClipboardTemp fso, dataFile, scriptFile
      Fail "start clipboard helper failed - " & Err.Description, 8
   End If
   Err.Clear
   Do While proc.Status = 0
      WScript.Sleep 100
   Loop
   stdout = Trim(proc.StdOut.ReadAll)
   stderr = Trim(proc.StdErr.ReadAll)
   If stdout <> "" Then WScript.Echo "INFO: " & stdout
   If proc.ExitCode <> 0 Then
      If stderr = "" Then stderr = "exit code " & proc.ExitCode
      CleanupClipboardTemp fso, dataFile, scriptFile
      Fail "set clipboard " & label & " failed - " & stderr, 8
   End If
   verifiedLineCount = ClipboardVerifiedLineCount(stdout)
   If verifiedLineCount <> CLng(expectedLineCount) Then
      CleanupClipboardTemp fso, dataFile, scriptFile
      Fail "set clipboard " & label & " failed - clipboard line count does not match expectedLineCount=" & expectedLineCount & "; " & stdout, 8
   End If

   CleanupClipboardTemp fso, dataFile, scriptFile
   WScript.Echo "INFO: " & label & " clipboard prepared count=" & expectedLineCount
   Err.Clear
End Sub

Function ClipboardVerifiedLineCount(value)
   Dim marker, pos, i, ch, digits
   marker = "lines="
   pos = InStr(CStr(value), marker)
   If pos <= 0 Then
      ClipboardVerifiedLineCount = -1
      Exit Function
   End If
   pos = pos + Len(marker)
   digits = ""
   For i = pos To Len(CStr(value))
      ch = Mid(CStr(value), i, 1)
      If ch >= "0" And ch <= "9" Then
         digits = digits & ch
      Else
         Exit For
      End If
   Next
   If digits = "" Then
      ClipboardVerifiedLineCount = -1
   Else
      ClipboardVerifiedLineCount = CLng(digits)
   End If
End Function

Sub CleanupClipboardTemp(fso, dataFile, scriptFile)
   Err.Clear
   If fso.FileExists(dataFile) Then fso.DeleteFile dataFile, True
   If fso.FileExists(scriptFile) Then fso.DeleteFile scriptFile, True
   Err.Clear
End Sub

Function ShellQuote(value)
   ShellQuote = """" & Replace(CStr(value), """", """""") & """"
End Function

Sub OpenTransaction()
   Err.Clear
   session.findById("wnd[0]").maximize
   session.findById("wnd[0]/tbar[0]/okcd").Text = "/n" & tcode
   session.findById("wnd[0]").sendVKey 0
   If Err.Number <> 0 Then Fail "open transaction failed - " & Err.Description, 3
   Err.Clear
   WaitReady 8000
   CheckSapStatus "open transaction"
End Sub

Sub PasteMaterialSelection()
   SetClipboardText materialText, materialCount, "material list"
   PressButton "wnd[0]/usr/btn%_S_MATNR_%_APP_%-VALU_PUSH", "open S_MATNR multiple selection", 8000
   PressButton "wnd[1]/tbar[0]/btn[24]", "paste S_MATNR material list", 8000
   PressButton "wnd[1]/tbar[0]/btn[8]", "confirm S_MATNR material list", 8000
End Sub

Sub FillWerksSelection()
   SetField "werks-low seed", "wnd[0]/usr/ctxtS_WERKS-LOW", plantSeedValue
   WScript.Echo "INFO: single S_WERKS plant uses LOW field"
End Sub

Function RunZfi057Window(index)
   Dim plantLogValue
   plantLogValue = Replace(plantText, vbCrLf, ",")
   WScript.Echo "INFO: zfi057 input group #" & index
   WScript.Echo "INFO: query ZFIT_RPA_BUKRS where GSBER=" & businessAreaValue & "; resolved WERKS=" & plantLogValue & "; plantCount=" & plantCount
   WScript.Echo "INFO: date input group #" & index & "; S_KADKY=[" & kadkyLow(index) & "~" & kadkyHigh(index) & "]; S_KADAT=[" & kadatLow(index) & "~" & kadatHigh(index) & "]"
   WScript.Echo "INFO: upstream material list was prepared by backend SAP NCo query before this VBS; materialCount=" & materialCount
   OpenTransaction
   SetField "kadky-low", "wnd[0]/usr/ctxtS_KADKY-LOW", kadkyLow(index)
   SetField "kadky-high", "wnd[0]/usr/ctxtS_KADKY-HIGH", kadkyHigh(index)
   SetField "mtart-low", "wnd[0]/usr/ctxtS_MTART-LOW", "*"
   SetField "kadat-low", "wnd[0]/usr/ctxtS_KADAT-LOW", kadatLow(index)
   SetField "kadat-high", "wnd[0]/usr/ctxtS_KADAT-HIGH", kadatHigh(index)
   FillWerksSelection
   PasteMaterialSelection
   If Not PressExecuteZfi057Window(index) Then
      RunZfi057Window = False
      Exit Function
   End If
   ExportAlvForWindow index
   RunZfi057Window = True
End Function

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
WScript.Echo "INFO: period=" & periodValue
WScript.Echo "INFO: weekEnd=" & weekEndValue
WScript.Echo "INFO: plants=" & plantsCsv
WScript.Echo "INFO: plantCount=" & plantCount
WScript.Echo "INFO: businessAreas=" & businessAreasCsv
WScript.Echo "INFO: businessArea=" & businessAreaValue
If factoryGroup <> "" Then WScript.Echo "INFO: factoryGroup=" & factoryGroup
WScript.Echo "INFO: zfi057 date window count=" & runCount

For i = 1 To runCount
   If RunZfi057Window(i) Then
      zfi057WindowSuccessCount = zfi057WindowSuccessCount + 1
   Else
      zfi057WindowNoDataCount = zfi057WindowNoDataCount + 1
   End If
Next

If zfi057WindowSuccessCount = 0 And zfi057WindowNoDataCount > 0 Then
   Fail "SAP status error after execute ZFI057 all groups - 没有符合条件数据", 6
End If

If zfi057WindowNoDataCount > 0 Then
   WScript.Echo "STATUS_TYPE=W"
   WScript.Echo "STATUS_TEXT=ZFI057 completed with partial no-data windows: success=" & zfi057WindowSuccessCount & ", noData=" & zfi057WindowNoDataCount
Else
   CheckSapStatus "finish"
End If
WScript.Echo "INFO: transaction script executed"
WScript.Quit 0
