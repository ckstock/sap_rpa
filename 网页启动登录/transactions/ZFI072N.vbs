' @tcode=ZFI072N
' @name=ZFI072N purchase price maintenance save
' @params=plants,period,weekEnd
' @dateRule=BUDAT_MONTH_TO_WEEK_END_WITH_CROSS_MONTH_SPLIT
' @factoryRule=single plant supplied by launcher/API; parent run batches multiple plants
'
' Standardized for SapWebLauncher. Source is ASCII/WSH safe.

On Error Resume Next

Dim tcode, plantsCsv, factoryGroup
Dim yearValue, weekValue, periodValue, weekEndValue
Dim plantValue, dateLowValues, dateHighValues, dateWindowCount
Dim SapGuiAuto, application, connection, session
Dim retries, sleepMs, statusType, statusText, windowIndex
Dim unresolvedOkCodeToken, unresolvedPlantsToken
Dim alvExportDir, alvExportFilename, alvOutputFile, alvExportReady, exportTimeoutMs, alvExportMethod
Dim unresolvedAlvExportDirToken, unresolvedAlvExportFilenameToken, currentWindowExportFileName

tcode = "{OK_CODE}"
plantsCsv = "{PLANTS}"
factoryGroup = "{FACTORY_GROUP}"
yearValue = "{YEAR}"
weekValue = "{WEEK}"
periodValue = "{PERIOD}"
weekEndValue = "{WEEK_END}"
alvExportDir = "{ALV_EXPORT_DIR}"
alvExportFilename = "{ALV_EXPORT_FILENAME}"
exportTimeoutMs = 180000
alvExportReady = False
alvExportMethod = ""
unresolvedOkCodeToken = "{" & "OK_CODE" & "}"
unresolvedPlantsToken = "{" & "PLANTS" & "}"
unresolvedAlvExportDirToken = "{" & "ALV_EXPORT_DIR" & "}"
unresolvedAlvExportFilenameToken = "{" & "ALV_EXPORT_FILENAME" & "}"

If Trim(CStr(tcode)) = "" Or Trim(CStr(tcode)) = unresolvedOkCodeToken Then tcode = "ZFI072N"
If UCase(Trim(CStr(tcode))) <> "ZFI072N" Then Fail "ZFI072N script refuses tcode=" & CStr(tcode), 10
If Trim(CStr(plantsCsv)) = unresolvedPlantsToken Then plantsCsv = ""
If Trim(CStr(alvExportDir)) = unresolvedAlvExportDirToken Then alvExportDir = ""
If Trim(CStr(alvExportFilename)) = unresolvedAlvExportFilenameToken Then alvExportFilename = ""
If IsPlaceholder(yearValue, "YEAR") Then yearValue = ""
If IsPlaceholder(weekValue, "WEEK") Then weekValue = ""
If IsPlaceholder(periodValue, "PERIOD") Then periodValue = ""
If IsPlaceholder(weekEndValue, "WEEK_END") Then weekEndValue = ""

plantValue = FirstCsvValue(plantsCsv)
If plantValue = "" Then Fail "ZFI072N requires one plant from {PLANTS}", 5
If CsvCount(plantsCsv) > 1 Then WScript.Echo "INFO: plantsCsv contains multiple values; using first plant=" & plantValue

ResolveZfi072nDateWindows

Function IsPlaceholder(value, tokenName)
   IsPlaceholder = (Trim(CStr(value)) = "{" & tokenName & "}")
End Function

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

Function CsvCount(value)
   Dim parts, item, count
   value = Replace(CStr(value), ";", ",")
   value = Replace(value, "|", ",")
   parts = Split(value, ",")
   count = 0
   For Each item In parts
      item = Trim(CStr(item))
      If item <> "" Then count = count + 1
   Next
   CsvCount = count
End Function

Function FormatSapDate(value)
   FormatSapDate = Year(value) & "." & Right("0" & Month(value), 2) & "." & Right("0" & Day(value), 2)
End Function

Function WeekStart(d)
   WeekStart = DateAdd("d", 1 - Weekday(d, vbMonday), d)
End Function

Function FirstDayOfMonth(d)
   FirstDayOfMonth = DateSerial(Year(d), Month(d), 1)
End Function

Function LastDayOfMonth(d)
   LastDayOfMonth = DateAdd("d", -1, DateSerial(Year(d), Month(d) + 1, 1))
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

Sub ResolveZfi072nDateWindows()
   Dim parsedStart, parsedEnd, defaultStart, defaultEnd
   defaultStart = DateAdd("d", -7, WeekStart(Date))
   defaultEnd = DateAdd("d", 6, defaultStart)
   parsedStart = ParseDateOrEmpty(periodValue)
   parsedEnd = ParseDateOrEmpty(weekEndValue)
   If IsEmpty(parsedStart) Then parsedStart = defaultStart
   If IsEmpty(parsedEnd) Then parsedEnd = defaultEnd
   If DateDiff("d", parsedStart, parsedEnd) < 0 Then Fail "ZFI072N weekEnd is before period", 5

   If Trim(CStr(yearValue)) = "" Then yearValue = Year(parsedStart)
   If Trim(CStr(weekValue)) = "" Then weekValue = DatePart("ww", parsedStart, vbMonday, vbFirstFourDays)

   If Year(parsedStart) = Year(parsedEnd) And Month(parsedStart) = Month(parsedEnd) Then
      ReDim dateLowValues(0)
      ReDim dateHighValues(0)
      dateWindowCount = 1
      dateLowValues(0) = FormatSapDate(FirstDayOfMonth(parsedEnd))
      dateHighValues(0) = FormatSapDate(parsedEnd)
   Else
      ReDim dateLowValues(1)
      ReDim dateHighValues(1)
      dateWindowCount = 2
      dateLowValues(0) = FormatSapDate(FirstDayOfMonth(parsedStart))
      dateHighValues(0) = FormatSapDate(LastDayOfMonth(parsedStart))
      dateLowValues(1) = FormatSapDate(FirstDayOfMonth(parsedEnd))
      dateHighValues(1) = FormatSapDate(parsedEnd)
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

Sub PressToolbarButton(id, label)
   Err.Clear
   session.findById(id).press
   If Err.Number <> 0 Then Fail label & " failed - " & Err.Description, 8
   WScript.Echo "INFO: pressed " & label
   Err.Clear
   WaitReady 600000
   CheckSapStatus label
End Sub

Sub PressExecute(label)
   PressToolbarButton "wnd[0]/tbar[1]/btn[8]", label
End Sub

Function IsNoDataStatusText(value)
   Dim text
   text = LCase(Trim(CStr(value)))
   IsNoDataStatusText = False
   If text = "" Then Exit Function
   If InStr(text, "no data") > 0 Or InStr(text, "no records") > 0 Then
      IsNoDataStatusText = True
      Exit Function
   End If
   If InStr(text, ChrW(&H6CA1) & ChrW(&H6709)) > 0 And _
      (InStr(text, ChrW(&H6570) & ChrW(&H636E)) > 0 Or InStr(text, ChrW(&H7B26) & ChrW(&H5408) & ChrW(&H6761) & ChrW(&H4EF6)) > 0) Then
      IsNoDataStatusText = True
      Exit Function
   End If
   If InStr(text, ChrW(&H65E0) & ChrW(&H6570) & ChrW(&H636E)) > 0 Then
      IsNoDataStatusText = True
      Exit Function
   End If
End Function

Function SapStatusIndicatesNoData(stage)
   SapStatusIndicatesNoData = False
   Err.Clear
   statusType = session.findById("wnd[0]/sbar").MessageType
   statusText = session.findById("wnd[0]/sbar").Text
   If Err.Number = 0 And Trim(CStr(statusText)) <> "" Then
      WScript.Echo "INFO: sap status after " & stage & " type=" & statusType & ", text=" & statusText
      If IsNoDataStatusText(statusText) Then
         statusType = "W"
         SapStatusIndicatesNoData = True
      End If
   End If
   Err.Clear
End Function

Function WaitForObject(id, timeoutMs)
   Dim waited, obj
   On Error Resume Next
   WaitForObject = False
   waited = 0
   Do While waited <= timeoutMs
      Err.Clear
      Set obj = session.findById(id)
      If Err.Number = 0 And IsObject(obj) Then
         WaitForObject = True
         Err.Clear
         Exit Function
      End If
      Err.Clear
      WScript.Sleep 100
      waited = waited + 100
   Loop
End Function

Function WaitForAnyObject(label, ids, timeoutMs)
   Dim waited, id, obj
   On Error Resume Next
   WaitForAnyObject = False
   waited = 0
   Do While waited <= timeoutMs
      For Each id In ids
         Err.Clear
         Set obj = session.findById(CStr(id))
         If Err.Number = 0 And IsObject(obj) Then
            WScript.Echo "INFO: ready " & label & " via " & id
            WaitForAnyObject = True
            Err.Clear
            Exit Function
         End If
         Err.Clear
      Next
      WScript.Sleep 200
      waited = waited + 200
   Loop
End Function

Function ObjectIsEnabled(id)
   Dim obj, enabledValue
   On Error Resume Next
   ObjectIsEnabled = False
   Err.Clear
   Set obj = session.findById(CStr(id))
   If Err.Number = 0 And IsObject(obj) Then
      Err.Clear
      enabledValue = obj.Enabled
      If Err.Number = 0 Then ObjectIsEnabled = CBool(enabledValue)
   End If
   Err.Clear
End Function

Function SafeObjectState(id)
   Dim obj, enabledValue, typeValue
   On Error Resume Next
   SafeObjectState = "missing"
   Err.Clear
   Set obj = session.findById(CStr(id))
   If Err.Number <> 0 Or Not IsObject(obj) Then
      Err.Clear
      Exit Function
   End If
   typeValue = ""
   enabledValue = ""
   Err.Clear
   typeValue = obj.Type
   If Err.Number <> 0 Then typeValue = "unknown"
   Err.Clear
   enabledValue = obj.Enabled
   If Err.Number <> 0 Then
      enabledValue = "unknown"
   Else
      enabledValue = CStr(enabledValue)
   End If
   SafeObjectState = "found type=" & CStr(typeValue) & " enabled=" & CStr(enabledValue)
   Err.Clear
End Function

Function SafeSessionInfo()
   Dim value
   On Error Resume Next
   SafeSessionInfo = ""
   Err.Clear
   value = session.Info.Transaction & "/" & session.Info.Program & "/" & session.Info.ScreenNumber
   If Err.Number = 0 Then SafeSessionInfo = CStr(value)
   Err.Clear
End Function

Sub EchoAlvExportDiagnostics(prefix)
   Dim currentStatusType, currentStatusText, windowTitle
   On Error Resume Next
   currentStatusType = ""
   currentStatusText = ""
   windowTitle = ""
   Err.Clear
   windowTitle = session.findById("wnd[0]").Text
   Err.Clear
   currentStatusType = session.findById("wnd[0]/sbar").MessageType
   currentStatusText = session.findById("wnd[0]/sbar").Text
   Err.Clear
   WScript.Echo prefix & ": session=" & SafeSessionInfo() & ", title=" & CStr(windowTitle)
   WScript.Echo prefix & ": status type=" & CStr(currentStatusType) & ", text=" & CStr(currentStatusText)
   WScript.Echo prefix & ": topBtn43=" & SafeObjectState("wnd[0]/tbar[1]/btn[43]")
   WScript.Echo prefix & ": grid1=" & SafeObjectState("wnd[0]/usr/cntlGRID1/shellcont/shell")
   WScript.Echo prefix & ": gridNested=" & SafeObjectState("wnd[0]/usr/cntlGRID1/shellcont/shell/shellcont[1]/shell")
   Err.Clear
End Sub

Function TryPressGridExportById(gridId)
   Dim gridObj, operationError
   On Error Resume Next
   TryPressGridExportById = False
   Err.Clear
   Set gridObj = session.findById(CStr(gridId))
   If Err.Number <> 0 Or Not IsObject(gridObj) Then
      Err.Clear
      Exit Function
   End If

   Err.Clear
   gridObj.pressToolbarContextButton "&MB_EXPORT"
   If Err.Number = 0 Then
      WScript.Sleep 300
      Err.Clear
      gridObj.selectContextMenuItem "&XXL"
      If Err.Number = 0 Then
         WScript.Echo "INFO: pressed Grid ALV export via " & CStr(gridId) & " &MB_EXPORT/&XXL"
         alvExportMethod = "grid-context"
         TryPressGridExportById = True
         Exit Function
      End If
   End If

   operationError = Err.Description
   Err.Clear
   gridObj.pressToolbarButton "&XXL"
   If Err.Number = 0 Then
      WScript.Echo "INFO: pressed Grid ALV export via " & CStr(gridId) & " &XXL"
      alvExportMethod = "grid-toolbar"
      TryPressGridExportById = True
      Exit Function
   End If

   WScript.Echo "WARN: Grid export attempts failed for " & CStr(gridId) & " - " & operationError & "; " & Err.Description
   Err.Clear
End Function

Function TryPressMenuExport()
   On Error Resume Next
   TryPressMenuExport = False
   Err.Clear
   session.findById("wnd[0]/mbar/menu[0]/menu[3]/menu[1]").select
   If Err.Number = 0 Then
      WScript.Echo "INFO: pressed menu ALV export via List/Export/Spreadsheet"
      alvExportMethod = "menu-list-export"
      TryPressMenuExport = True
      Exit Function
   End If
   Err.Clear
End Function

Function WaitForAlvExportEntry(timeoutMs)
   Dim waited
   WaitForAlvExportEntry = False
   waited = 0
   Do While waited <= timeoutMs
      If ObjectExists("wnd[0]/tbar[1]/btn[43]") Then
         WScript.Echo "INFO: ready ALV export button object via wnd[0]/tbar[1]/btn[43]"
         alvExportMethod = "top-toolbar"
         WaitForAlvExportEntry = True
         Exit Function
      End If
      If ObjectExists("wnd[0]/usr/cntlGRID1/shellcont/shell") Then
         WScript.Echo "INFO: ready ALV grid via wnd[0]/usr/cntlGRID1/shellcont/shell"
         alvExportMethod = "grid"
         WaitForAlvExportEntry = True
         Exit Function
      End If
      If ObjectExists("wnd[0]/usr/cntlGRID1/shellcont/shell/shellcont[1]/shell") Then
         WScript.Echo "INFO: ready nested ALV grid via wnd[0]/usr/cntlGRID1/shellcont/shell/shellcont[1]/shell"
         alvExportMethod = "grid-nested"
         WaitForAlvExportEntry = True
         Exit Function
      End If
      WScript.Sleep 200
      waited = waited + 200
   Loop
End Function

Function PressAlvExportEntry(timeoutMs)
   PressAlvExportEntry = False
   If Not WaitForAlvExportEntry(timeoutMs) Then
      EchoAlvExportDiagnostics "ERROR_CONTEXT"
      Exit Function
   End If

   If ObjectExists("wnd[0]/tbar[1]/btn[43]") Then
      Err.Clear
      session.findById("wnd[0]/tbar[1]/btn[43]").press
      If Err.Number = 0 Then
         WScript.Echo "INFO: pressed ALV export button"
         alvExportMethod = "top-toolbar"
         PressAlvExportEntry = True
         Exit Function
      End If
      WScript.Echo "WARN: top ALV export button press failed - " & Err.Description
      Err.Clear
   End If

   If TryPressGridExportById("wnd[0]/usr/cntlGRID1/shellcont/shell") Then
      PressAlvExportEntry = True
      Exit Function
   End If

   If TryPressGridExportById("wnd[0]/usr/cntlGRID1/shellcont/shell/shellcont[1]/shell") Then
      PressAlvExportEntry = True
      Exit Function
   End If

   If TryPressMenuExport() Then
      PressAlvExportEntry = True
      Exit Function
   End If

   EchoAlvExportDiagnostics "ERROR_CONTEXT"
End Function

Function CombinePath(folderPath, fileName)
   If Right(CStr(folderPath), 1) = "\" Then
      CombinePath = CStr(folderPath) & CStr(fileName)
   Else
      CombinePath = CStr(folderPath) & "\" & CStr(fileName)
   End If
End Function

Function EnsureFolderExists(folderPath)
   Dim fso, parentPath
   On Error Resume Next
   EnsureFolderExists = False
   If Trim(CStr(folderPath)) = "" Then Exit Function
   Err.Clear
   Set fso = CreateObject("Scripting.FileSystemObject")
   If Err.Number <> 0 Then Fail "create FileSystemObject failed - " & Err.Description, 8
   If fso.FolderExists(folderPath) Then
      EnsureFolderExists = True
      Exit Function
   End If
   parentPath = fso.GetParentFolderName(folderPath)
   If parentPath <> "" And Not fso.FolderExists(parentPath) Then
      If Not EnsureFolderExists(parentPath) Then Exit Function
   End If
   Err.Clear
   fso.CreateFolder folderPath
   If Err.Number <> 0 Then Fail "create export folder failed: " & folderPath & " - " & Err.Description, 8
   EnsureFolderExists = fso.FolderExists(folderPath)
   Err.Clear
End Function

Function DeleteFileIfExists(filePath)
   Dim fso
   On Error Resume Next
   DeleteFileIfExists = False
   Err.Clear
   Set fso = CreateObject("Scripting.FileSystemObject")
   If Err.Number <> 0 Then Fail "create FileSystemObject failed before ALV export cleanup - " & Err.Description, 8
   If fso.FileExists(filePath) Then
      Err.Clear
      fso.DeleteFile filePath, True
      If Err.Number <> 0 Then Fail "delete existing ALV export file failed: " & filePath & " - " & Err.Description, 8
      WScript.Echo "INFO: deleted existing ALV export file before export"
   End If
   DeleteFileIfExists = True
   Err.Clear
End Function

Function FileExistsAndNotEmpty(filePath)
   Dim fso
   On Error Resume Next
   FileExistsAndNotEmpty = False
   If Trim(CStr(filePath)) = "" Then Exit Function
   Err.Clear
   Set fso = CreateObject("Scripting.FileSystemObject")
   If Err.Number = 0 And fso.FileExists(filePath) Then
      FileExistsAndNotEmpty = (CLng(fso.GetFile(filePath).Size) > 0)
   End If
   Err.Clear
End Function

Function WaitForFileReady(filePath, timeoutMs)
   Dim fso, waited, size1, size2
   On Error Resume Next
   WaitForFileReady = False
   waited = 0
   Set fso = CreateObject("Scripting.FileSystemObject")
   Do While waited <= timeoutMs
      Err.Clear
      If fso.FileExists(filePath) Then
         size1 = fso.GetFile(filePath).Size
         WScript.Sleep 500
         size2 = fso.GetFile(filePath).Size
         If Err.Number = 0 And CLng(size2) > 0 And CLng(size1) = CLng(size2) Then
            WaitForFileReady = True
            Err.Clear
            Exit Function
         End If
      End If
      Err.Clear
      WScript.Sleep 500
      waited = waited + 1000
   Loop
End Function

Function ExportedExcelProcessExists(filePath)
   Dim fso, wmi, processes, proc, targetPath, commandLine
   On Error Resume Next
   ExportedExcelProcessExists = False
   If Trim(CStr(filePath)) = "" Then Exit Function
   Set fso = CreateObject("Scripting.FileSystemObject")
   targetPath = LCase(Replace(fso.GetAbsolutePathName(CStr(filePath)), "/", "\"))
   Err.Clear
   Set wmi = GetObject("winmgmts:\\.\root\cimv2")
   If Err.Number <> 0 Or Not IsObject(wmi) Then
      Err.Clear
      Exit Function
   End If
   Set processes = wmi.ExecQuery("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='EXCEL.EXE'")
   If Err.Number <> 0 Then
      Err.Clear
      Exit Function
   End If
   For Each proc In processes
      commandLine = ""
      If Not IsNull(proc.CommandLine) Then commandLine = LCase(Replace(CStr(proc.CommandLine), "/", "\"))
      If InStr(commandLine, targetPath) > 0 Then
         ExportedExcelProcessExists = True
         Exit Function
      End If
   Next
   Err.Clear
End Function

Function TerminateExportedExcelProcesses(filePath)
   Dim fso, wmi, processes, proc, targetPath, commandLine, result
   On Error Resume Next
   TerminateExportedExcelProcesses = 0
   If Trim(CStr(filePath)) = "" Then Exit Function
   Set fso = CreateObject("Scripting.FileSystemObject")
   targetPath = LCase(Replace(fso.GetAbsolutePathName(CStr(filePath)), "/", "\"))
   Err.Clear
   Set wmi = GetObject("winmgmts:\\.\root\cimv2")
   If Err.Number <> 0 Or Not IsObject(wmi) Then
      WScript.Echo "WARN: WMI unavailable for path-scoped Excel cleanup - " & Err.Description
      Err.Clear
      Exit Function
   End If
   Set processes = wmi.ExecQuery("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='EXCEL.EXE'")
   If Err.Number <> 0 Then
      WScript.Echo "WARN: failed to query Excel processes for export cleanup - " & Err.Description
      Err.Clear
      Exit Function
   End If
   For Each proc In processes
      commandLine = ""
      If Not IsNull(proc.CommandLine) Then commandLine = LCase(Replace(CStr(proc.CommandLine), "/", "\"))
      If InStr(commandLine, targetPath) > 0 Then
         Err.Clear
         result = proc.Terminate()
         If Err.Number = 0 Then
            TerminateExportedExcelProcesses = TerminateExportedExcelProcesses + 1
            WScript.Echo "INFO: terminated exported Excel process pid=" & CStr(proc.ProcessId) & " for " & CStr(filePath)
         Else
            WScript.Echo "WARN: failed to terminate exported Excel process pid=" & CStr(proc.ProcessId) & " - " & Err.Description
         End If
         Err.Clear
      End If
   Next
End Function

Function WaitForExportedExcelClosed(filePath, timeoutMs)
   Dim waited
   On Error Resume Next
   WaitForExportedExcelClosed = True
   If Trim(CStr(filePath)) = "" Then Exit Function
   waited = 0
   Do While waited <= timeoutMs
      If Not ExportedExcelProcessExists(filePath) Then
         WaitForExportedExcelClosed = True
         Exit Function
      End If
      WaitForExportedExcelClosed = False
      WScript.Sleep 500
      waited = waited + 500
   Loop
End Function

Sub ScheduleExcelCloseHelper(targetPath)
   Dim fso, sh, helperDir, helperPath, ts, commandLine, targetName
   On Error Resume Next
   If Trim(CStr(targetPath)) = "" Then Exit Sub
   Set fso = CreateObject("Scripting.FileSystemObject")
   Set sh = CreateObject("WScript.Shell")
   targetName = fso.GetFileName(CStr(targetPath))
   helperDir = fso.BuildPath(fso.GetParentFolderName(CStr(targetPath)), "_excel_close")
   If Not fso.FolderExists(helperDir) Then fso.CreateFolder helperDir
   helperPath = fso.BuildPath(helperDir, "sap_rpa_close_excel_" & Replace(Replace(CStr(targetName), ".", "_"), "-", "_") & ".vbs")
   Set ts = fso.CreateTextFile(helperPath, True, False)
   If Err.Number <> 0 Then
      WScript.Echo "WARN: failed to create delayed Excel close helper - " & Err.Description
      Err.Clear
      Exit Sub
   End If
   ts.WriteLine "On Error Resume Next"
   ts.WriteLine "target = LCase(Replace(CStr(WScript.Arguments.Item(0)), ""/"", ""\""))"
   ts.WriteLine "waited = 0"
   ts.WriteLine "Do While waited <= 30000"
   ts.WriteLine "  Err.Clear"
   ts.WriteLine "  Set app = GetObject(, ""Excel.Application"")"
   ts.WriteLine "  If Err.Number = 0 And IsObject(app) Then"
   ts.WriteLine "    app.DisplayAlerts = False"
   ts.WriteLine "    closed = 0"
   ts.WriteLine "    For i = app.Workbooks.Count To 1 Step -1"
   ts.WriteLine "      Err.Clear"
   ts.WriteLine "      Set wb = app.Workbooks.Item(CInt(i))"
   ts.WriteLine "      fullName = """""
   ts.WriteLine "      If Err.Number = 0 Then fullName = LCase(Replace(CStr(wb.FullName), ""/"", ""\""))"
   ts.WriteLine "      If Err.Number = 0 And fullName = target Then"
   ts.WriteLine "        wb.Close False"
   ts.WriteLine "        closed = closed + 1"
   ts.WriteLine "      End If"
   ts.WriteLine "      Err.Clear"
   ts.WriteLine "    Next"
   ts.WriteLine "    If closed > 0 Then"
   ts.WriteLine "      WScript.Sleep 500"
   ts.WriteLine "      If app.Workbooks.Count = 0 Then app.Quit"
   ts.WriteLine "      WScript.Quit 0"
   ts.WriteLine "    End If"
   ts.WriteLine "  End If"
   ts.WriteLine "  Err.Clear"
   ts.WriteLine "  WScript.Sleep 500"
   ts.WriteLine "  waited = waited + 500"
   ts.WriteLine "Loop"
   ts.WriteLine "WScript.Quit 0"
   ts.Close
   commandLine = """" & WScript.FullName & """ //B //Nologo """ & helperPath & """ """ & CStr(targetPath) & """"
   sh.Run commandLine, 0, False
   If Err.Number <> 0 Then
      WScript.Echo "WARN: failed to start delayed Excel close helper - " & Err.Description
      Err.Clear
   End If
End Sub

Sub CloseExportedExcelWorkbook(filePath)
   Dim excelApp, wb, i, targetPath, workbookPath, targetName, closedCount, fso, waited, terminatedCount
   On Error Resume Next
   closedCount = 0
   If Trim(CStr(filePath)) = "" Then Exit Sub
   Set fso = CreateObject("Scripting.FileSystemObject")
   targetPath = LCase(Replace(fso.GetAbsolutePathName(CStr(filePath)), "/", "\"))
   targetName = LCase(fso.GetFileName(CStr(filePath)))
   waited = 0
   Do While waited <= 10000 And closedCount = 0
      Err.Clear
      Set excelApp = GetObject(, "Excel.Application")
      If Err.Number = 0 And IsObject(excelApp) Then
         excelApp.DisplayAlerts = False
         For i = excelApp.Workbooks.Count To 1 Step -1
            Err.Clear
            Set wb = excelApp.Workbooks.Item(CInt(i))
            If Err.Number = 0 And IsObject(wb) Then
               workbookPath = LCase(Replace(CStr(wb.FullName), "/", "\"))
               If workbookPath = targetPath Then
                  Err.Clear
                  wb.Close False
                  If Err.Number = 0 Then
                     closedCount = closedCount + 1
                     WScript.Echo "INFO: closed exported Excel workbook=" & CStr(filePath)
                  Else
                     WScript.Echo "WARN: failed to close exported Excel workbook - " & Err.Description
                  End If
               End If
            End If
            Err.Clear
         Next
      End If
      If closedCount = 0 Then
         WScript.Sleep 500
         waited = waited + 500
      End If
   Loop
   If closedCount > 0 Then
      WScript.Sleep 1000
      Err.Clear
      If excelApp.Workbooks.Count = 0 Then
         excelApp.Quit
         WScript.Echo "INFO: quit Excel after exported workbook close"
      End If
   Else
      ScheduleExcelCloseHelper filePath
      WScript.Echo "INFO: exported Excel workbook not visible yet; scheduled delayed close for " & targetName
   End If

   If ExportedExcelProcessExists(filePath) Then
      terminatedCount = TerminateExportedExcelProcesses(filePath)
      If terminatedCount > 0 Then
         If Not WaitForExportedExcelClosed(filePath, 15000) Then
            Fail "exported Excel process still open after path-scoped cleanup: " & CStr(filePath), 8
         End If
      Else
         If Not WaitForExportedExcelClosed(filePath, 15000) Then
            Fail "exported Excel process still open and could not be cleaned: " & CStr(filePath), 8
         End If
      End If
   End If
   Err.Clear
End Sub

Sub ConfirmExportOverwriteIfPresent()
   Dim confirmTry, obj
   On Error Resume Next
   For confirmTry = 1 To 5
      WScript.Sleep 300
      If Not ObjectExists("wnd[1]") Then Exit For
      Err.Clear
      Set obj = session.findById("wnd[1]/usr/btnSPOP-OPTION1")
      If Err.Number = 0 And IsObject(obj) Then
         obj.press
         WScript.Echo "INFO: confirmed ALV export overwrite via OPTION1"
      Else
         Err.Clear
         Set obj = session.findById("wnd[1]/tbar[0]/btn[0]")
         If Err.Number = 0 And IsObject(obj) And Not ObjectExists("wnd[1]/usr/ctxtDY_PATH") Then
            obj.press
            WScript.Echo "INFO: confirmed ALV export dialog via toolbar OK"
         End If
      End If
      Err.Clear
   Next
End Sub

Function BuildWindowExportFilename(baseFileName, index)
   Dim fso, stem, ext
   On Error Resume Next
   BuildWindowExportFilename = CStr(baseFileName)
   If dateWindowCount <= 1 Then Exit Function
   Set fso = CreateObject("Scripting.FileSystemObject")
   stem = fso.GetBaseName(CStr(baseFileName))
   ext = fso.GetExtensionName(CStr(baseFileName))
   If Trim(CStr(stem)) = "" Then stem = "ZFI072N"
   If Trim(CStr(ext)) = "" Then
      BuildWindowExportFilename = stem & "_part" & CStr(index + 1) & ".xlsx"
   Else
      BuildWindowExportFilename = stem & "_part" & CStr(index + 1) & "." & ext
   End If
   Err.Clear
End Function

Function ExportAlvIfConfigured(exportDir, exportFilename, timeoutMs)
   On Error Resume Next
   ExportAlvIfConfigured = False
   alvExportReady = False
   alvOutputFile = ""
   exportDir = Trim(CStr(exportDir))
   exportFilename = Trim(CStr(exportFilename))
   WScript.Echo "INFO: ALV export target dir=" & exportDir & ", filename=" & exportFilename
   If exportDir = "" Or exportFilename = "" Then Fail "ALV export target is empty for ZFI072N", 8
   If Not EnsureFolderExists(exportDir) Then Fail "ALV export folder could not be prepared: " & exportDir, 8
   alvOutputFile = CombinePath(exportDir, exportFilename)
   If Not DeleteFileIfExists(alvOutputFile) Then Fail "ALV export target could not be cleared before export: " & alvOutputFile, 8

   If Not PressAlvExportEntry(timeoutMs) Then Fail "ALV export entry not found or not usable before timeout", 8

   If Not WaitForObject("wnd[1]", 10000) Then Fail "ALV export dialog did not open", 8
   If Not ObjectExists("wnd[1]/usr/ctxtDY_PATH") Then
      Err.Clear
      session.findById("wnd[1]/tbar[0]/btn[0]").press
      If Err.Number <> 0 Then Fail "confirm ALV export format dialog failed - " & Err.Description, 8
      WScript.Echo "INFO: confirmed ALV export format dialog"
   End If

   If Not WaitForObject("wnd[1]/usr/ctxtDY_PATH", 10000) Then Fail "ALV export path field did not appear", 8
   Err.Clear
   session.findById("wnd[1]/usr/ctxtDY_PATH").Text = exportDir
   session.findById("wnd[1]/usr/ctxtDY_FILENAME").Text = exportFilename
   session.findById("wnd[1]/usr/ctxtDY_FILENAME").caretPosition = Len(exportFilename)
   session.findById("wnd[1]/tbar[0]/btn[0]").press
   If Err.Number <> 0 Then Fail "submit ALV export file path failed - " & Err.Description, 8
   WScript.Echo "INFO: submitted ALV export file=" & alvOutputFile

   ConfirmExportOverwriteIfPresent
   If Not WaitForFileReady(alvOutputFile, timeoutMs) Then Fail "ALV export file was not created before timeout: " & alvOutputFile, 8

   WScript.Echo "INFO: ALV export file ready=" & alvOutputFile
   CloseExportedExcelWorkbook alvOutputFile
   WScript.Echo "INFO: exported Excel cleanup confirmed=" & alvOutputFile
   WScript.Echo "OUTPUT_FILE=" & alvOutputFile
   alvExportReady = True
   ExportAlvIfConfigured = True
End Function

Sub SelectAllGrid()
   Err.Clear
   session.findById("wnd[0]/usr/cntlGRID1/shellcont/shell").setCurrentCell -1, ""
   session.findById("wnd[0]/usr/cntlGRID1/shellcont/shell").selectAll
   If Err.Number <> 0 Then Fail "select all grid failed - " & Err.Description, 8
   WScript.Echo "INFO: selected all grid rows"
   Err.Clear
End Sub

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

Sub ExecuteWindow(index)
   Dim lowValue, highValue, label
   lowValue = dateLowValues(index)
   highValue = dateHighValues(index)
   label = "group #" & CStr(index + 1)

   WScript.Echo "INFO: date input group #" & CStr(index + 1) & "; S_BUDAT=[" & lowValue & "~" & highValue & "]"
   OpenTransaction
   SetField "werks-low", "wnd[0]/usr/ctxtS_WERKS-LOW", plantValue
   SetField "budat-low", "wnd[0]/usr/ctxtS_BUDAT-LOW", lowValue
   SetField "budat-high", "wnd[0]/usr/ctxtS_BUDAT-HIGH", highValue
   Err.Clear
   session.findById("wnd[0]/usr/ctxtS_BUDAT-HIGH").setFocus
   session.findById("wnd[0]/usr/ctxtS_BUDAT-HIGH").caretPosition = Len(highValue)
   Err.Clear
   PressExecute "execute " & label
   WaitReady 1200000
   If SapStatusIndicatesNoData("execute " & label) Then
      WScript.Echo "WARN: skip ALV export/save for " & label & " because SAP returned no data"
      Exit Sub
   End If
   currentWindowExportFileName = BuildWindowExportFilename(alvExportFilename, index)
   If Not ExportAlvIfConfigured(alvExportDir, currentWindowExportFileName, exportTimeoutMs) Then Fail "ALV export returned false for " & label, 8
   If Not FileExistsAndNotEmpty(alvOutputFile) Then Fail "ALV export file missing or empty before save: " & alvOutputFile, 8
   SelectAllGrid
   PressToolbarButton "wnd[0]/tbar[1]/btn[14]", "save selected rows " & label
   CheckSapStatus "finish " & label
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
WScript.Echo "INFO: dateWindowCount=" & dateWindowCount
If plantValue <> "" Then WScript.Echo "INFO: plant=" & plantValue
If factoryGroup <> "" And Not IsPlaceholder(factoryGroup, "FACTORY_GROUP") Then WScript.Echo "INFO: factoryGroup=" & factoryGroup

For windowIndex = 0 To dateWindowCount - 1
   ExecuteWindow windowIndex
Next

CheckSapStatus "finish"
WScript.Echo "STATUS_TYPE=S"
WScript.Echo "STATUS_TEXT=Automated transaction finished"
WScript.Echo "INFO: transaction script executed"
WScript.Quit 0
