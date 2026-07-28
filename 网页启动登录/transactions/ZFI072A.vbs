' @tcode=ZFI072A
' @name=ZFI072A purchase price monthly report
' @params=year,week,plants
' @dateRule=LAST_WEEK_ISO
' @factoryRule=plants are supplied by launcher/API
'
' Generated from SAP GUI Recorder output. Review SAP operation block before production use.

On Error Resume Next

Dim tcode, plantsCsv, factoryGroup, parentRunId, unresolvedPlantsToken, unresolvedOkCodeToken, unresolvedParentRunIdToken
Dim targetSystem, targetClient, targetUser, unresolvedSapSystemToken, unresolvedSapClientToken, unresolvedSapUserToken
Dim targetDate, yearValue, weekValue, pageYear, pageWeek, periodValue, weekEndValue
Dim plantValue, setOk, noDataResult, alvExportReady, alvResult, fatalError, fatalExitCode
Dim SapGuiAuto, application, connection, session, connIndex, sessIndex
Dim retries, maxRetries, sleepMs, statusType, statusText, operationError
Dim longRunTimeoutMs, saveTimeoutMs, saveButtonTimeoutMs, exportTimeoutMs, sapCloseOk, shouldCloseSapAtEnd
Dim alvExportDir, alvExportFilename, alvOutputFile, unresolvedAlvExportDirToken, unresolvedAlvExportFilenameToken

tcode = "{OK_CODE}"
targetSystem = "{SAP_SYSTEM}"
targetClient = "{SAP_CLIENT}"
targetUser = "{SAP_USER}"
plantsCsv = "{PLANTS}"
factoryGroup = "{FACTORY_GROUP}"
parentRunId = "{PARENT_RUN_ID}"
pageYear = "{YEAR}"
pageWeek = "{WEEK}"
periodValue = "{PERIOD}"
weekEndValue = "{WEEK_END}"
alvExportDir = "{ALV_EXPORT_DIR}"
alvExportFilename = "{ALV_EXPORT_FILENAME}"
maxRetries = 100
longRunTimeoutMs = 3600000
If CsvContains(plantsCsv, "9301") Then longRunTimeoutMs = 7200000
saveTimeoutMs = 600000
saveButtonTimeoutMs = 120000
exportTimeoutMs = 180000
noDataResult = False
alvExportReady = False
fatalError = False
fatalExitCode = 0
unresolvedPlantsToken = "{" & "PLANTS" & "}"
unresolvedOkCodeToken = "{" & "OK_CODE" & "}"
unresolvedParentRunIdToken = "{" & "PARENT_RUN_ID" & "}"
unresolvedSapSystemToken = "{" & "SAP_SYSTEM" & "}"
unresolvedSapClientToken = "{" & "SAP_CLIENT" & "}"
unresolvedSapUserToken = "{" & "SAP_USER" & "}"
unresolvedAlvExportDirToken = "{" & "ALV_EXPORT_DIR" & "}"
unresolvedAlvExportFilenameToken = "{" & "ALV_EXPORT_FILENAME" & "}"

If Trim(CStr(tcode)) = "" Or Trim(CStr(tcode)) = unresolvedOkCodeToken Then tcode = "ZFI072A"
If Trim(CStr(parentRunId)) = unresolvedParentRunIdToken Then parentRunId = ""
If Trim(CStr(targetSystem)) = unresolvedSapSystemToken Then targetSystem = ""
If Trim(CStr(targetClient)) = unresolvedSapClientToken Then targetClient = ""
If Trim(CStr(targetUser)) = unresolvedSapUserToken Then targetUser = ""
If Trim(CStr(alvExportDir)) = unresolvedAlvExportDirToken Then alvExportDir = ""
If Trim(CStr(alvExportFilename)) = unresolvedAlvExportFilenameToken Then alvExportFilename = ""
If UCase(Trim(CStr(tcode))) <> "ZFI072A" Then
   EmitError "ZFI072A script refuses non-ZFI072A tcode=" & CStr(tcode)
   WScript.Quit 10
End If
shouldCloseSapAtEnd = (Trim(CStr(parentRunId)) = "")

Sub EmitError(message)
   If Trim(CStr(statusType)) <> "" Or Trim(CStr(statusText)) <> "" Then
      WScript.Echo "SAP_STATUS_TYPE=" & statusType
      WScript.Echo "SAP_STATUS_TEXT=" & statusText
   End If
   statusType = "E"
   statusText = CStr(message)
   WScript.Echo "STATUS_TYPE=" & statusType
   WScript.Echo "STATUS_TEXT=" & statusText
   WScript.Echo "ERROR=" & message
   WScript.Echo "ERROR: " & message
End Sub

Sub HardQuit(exitCode)
   Err.Clear
   On Error Resume Next
   WScript.Quit CInt(exitCode)
   On Error GoTo 0
   Err.Raise vbObjectError + 7201, "ZFI072A", "WScript.Quit returned unexpectedly; exitCode=" & CStr(exitCode)
End Sub

Sub FailAndQuit(message, exitCode)
   fatalError = True
   fatalExitCode = CInt(exitCode)
   alvExportReady = False
   alvOutputFile = ""
   EmitError message
   WScript.Echo "FATAL_EXIT_CODE=" & CStr(fatalExitCode)
   If shouldCloseSapAtEnd Then
      Err.Clear
      sapCloseOk = CloseSapSession()
   Else
      WScript.Echo "INFO: defer SAP cleanup to parent after batch completion"
   End If
End Sub

Sub StopIfFatal(context)
   If fatalError Then
      WScript.Echo "FATAL_ABORT=" & CStr(context)
   End If
End Sub

Function DoneStatusText()
   DoneStatusText = ChrW(&H81EA) & ChrW(&H52A8) & ChrW(&H5316) & ChrW(&H5DF2) & ChrW(&H8DD1) & ChrW(&H5B8C)
End Function
Function BoolText(value)
   If CBool(value) Then
      BoolText = "true"
   Else
      BoolText = "false"
   End If
End Function

Function NoDataStatusText()
   NoDataStatusText = ChrW(&H6CA1) & ChrW(&H6709) & ChrW(&H7B26) & ChrW(&H5408) & ChrW(&H6761) & ChrW(&H4EF6) & ChrW(&H6570) & ChrW(&H636E)
End Function

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

If Trim(CStr(plantsCsv)) = "" Or Trim(CStr(plantsCsv)) = unresolvedPlantsToken Then
   EmitError "ZFI072A requires plants from launcher input/API selection"
   WScript.Quit 5
End If
If CsvCount(plantsCsv) > 1 Then
   FailAndQuit "ZFI072A child script requires one plant per run; launcher must split plants before invoking VBS. plants=" & CStr(plantsCsv), 8
End If

targetDate = DateAdd("d", -7, Date)
yearValue = Year(targetDate)
weekValue = DatePart("ww", targetDate, vbMonday, vbFirstFourDays)
If pageYear <> "" Then yearValue = pageYear
If pageWeek <> "" Then weekValue = pageWeek

Function ObjectExists(id)
   Dim obj
   On Error Resume Next
   Err.Clear
   Set obj = session.findById(id)
   ObjectExists = (Err.Number = 0 And IsObject(obj))
   Err.Clear
End Function

Function FirstCsvValue(value)
   Dim parts, item
   value = Replace(value, ";", ",")
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

Function CsvToClipboardText(value)
   Dim parts, item, result
   value = Replace(value, ";", ",")
   value = Replace(value, "|", ",")
   parts = Split(value, ",")
   result = ""
   For Each item In parts
      item = Trim(CStr(item))
      If item <> "" Then
         If result <> "" Then result = result & vbCrLf
         result = result & item
      End If
   Next
   CsvToClipboardText = result
End Function

Function CsvCount(value)
   Dim parts, item, count
   value = Replace(value, ";", ",")
   value = Replace(value, "|", ",")
   parts = Split(value, ",")
   count = 0
   For Each item In parts
      item = Trim(CStr(item))
      If item <> "" Then count = count + 1
   Next
   CsvCount = count
End Function

Function CsvContains(value, expected)
   Dim parts, item
   CsvContains = False
   value = Replace(value, ";", ",")
   value = Replace(value, "|", ",")
   parts = Split(value, ",")
   For Each item In parts
      If UCase(Trim(CStr(item))) = UCase(Trim(CStr(expected))) Then
         CsvContains = True
         Exit Function
      End If
   Next
End Function

Function SetClipboardText(value)
   Dim sh, exec
   SetClipboardText = False
   Err.Clear
   Set sh = CreateObject("WScript.Shell")
   Set exec = sh.Exec("%ComSpec% /c clip")
   exec.StdIn.Write value
   exec.StdIn.Close
   Do While exec.Status = 0
      WScript.Sleep 100
   Loop
   If Err.Number = 0 And exec.ExitCode = 0 Then SetClipboardText = True
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

Function PressButtonByCandidates(label, ids)
   Dim id, obj
   On Error Resume Next
   PressButtonByCandidates = False
   For Each id In ids
      Err.Clear
      Set obj = session.findById(CStr(id))
      If Err.Number = 0 And IsObject(obj) Then
         obj.press
         If Err.Number = 0 Then
            WScript.Echo "INFO: pressed " & label & " via " & id
            PressButtonByCandidates = True
            Err.Clear
            Exit Function
         End If
      End If
      Err.Clear
   Next
   EmitError "SAP button not found for " & label
End Function

Function WaitForSessionReady(timeoutMs)
   Dim waited, busyNow
   On Error Resume Next
   WaitForSessionReady = False
   waited = 0
   Do While waited <= timeoutMs
      Err.Clear
      busyNow = session.Busy
      If Err.Number = 0 Then
         If Not CBool(busyNow) Then
            WaitForSessionReady = True
            Err.Clear
            Exit Function
         End If
      End If
      Err.Clear
      WScript.Sleep 200
      waited = waited + 200
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
      If Err.Number = 0 Then
         ObjectIsEnabled = CBool(enabledValue)
      End If
   End If
   Err.Clear
End Function

Function WaitForAnyEnabledObject(label, ids, timeoutMs)
   Dim waited, id
   On Error Resume Next
   WaitForAnyEnabledObject = False
   waited = 0
   Do While waited <= timeoutMs
      For Each id In ids
         If ObjectIsEnabled(CStr(id)) Then
            WScript.Echo "INFO: ready enabled " & label & " via " & id
            WaitForAnyEnabledObject = True
            Exit Function
         End If
      Next
      WScript.Sleep 200
      waited = waited + 200
   Loop
End Function

Function IsAlvOutputReady()
   Dim programName, screenNumber
   On Error Resume Next
   IsAlvOutputReady = False
   If Not IsObject(session) Then Exit Function
   programName = UCase(Trim(CStr(SafeSessionValue(session, "Program"))))
   screenNumber = Trim(CStr(SafeSessionValue(session, "ScreenNumber")))
   If programName = "SAPLSLVC_FULLSCREEN" And (screenNumber = "500" Or screenNumber = "0500") Then
      IsAlvOutputReady = True
      Exit Function
   End If
   If ObjectExists("wnd[0]/tbar[1]/btn[43]") Then
      IsAlvOutputReady = True
      Exit Function
   End If
End Function

Function WaitForPostExecuteReady(timeoutMs)
   Dim waited, readyLogged
   On Error Resume Next
   WaitForPostExecuteReady = False
   waited = 0
   readyLogged = False
   WScript.Echo "INFO: wait for SAP execute processing, timeoutMs=" & CStr(timeoutMs)
   Do While waited <= timeoutMs
      If IsAlvOutputReady() Then
         WScript.Echo "INFO: SAP execute processing finished, ALV output screen/button detected before session ready check"
         WaitForPostExecuteReady = True
         Err.Clear
         EchoSessionContext "ALV_READY_CONTEXT"
         Err.Clear
         Exit Function
      End If
      If WaitForSessionReady(1000) Then
         Err.Clear
         statusType = session.findById("wnd[0]/sbar").MessageType
         statusText = session.findById("wnd[0]/sbar").Text
         If Err.Number = 0 Then
            If statusText <> "" Then WScript.Echo "INFO: post execute status type=" & statusType & ", text=" & statusText
            If IsNoDataStatusText(statusText) Then
               noDataResult = True
               statusType = "W"
               statusText = NoDataStatusText()
               WScript.Echo "WARN: SAP ALV has no data; skip save/export for this plant"
               WaitForPostExecuteReady = True
               Err.Clear
               Exit Function
            End If
            If statusType = "E" Or statusType = "A" Then
               EmitError "SAP execute rejected - " & statusText
               Err.Clear
               Exit Function
            End If
         End If
         Err.Clear
         If ObjectIsEnabled("wnd[0]/tbar[1]/btn[14]") Then
            WScript.Echo "INFO: SAP execute processing finished, save button is enabled"
            WaitForPostExecuteReady = True
            Exit Function
         End If
         If ObjectIsEnabled("wnd[0]/tbar[1]/btn[11]") Then
            WScript.Echo "INFO: SAP execute processing finished, alternate save button is enabled"
            WaitForPostExecuteReady = True
            Exit Function
         End If
          If ObjectIsEnabled("wnd[0]/tbar[1]/btn[43]") Then
             WScript.Echo "INFO: SAP execute processing finished, ALV export button is enabled"
             WaitForPostExecuteReady = True
             Exit Function
          End If
         If IsAlvOutputReady() Then
            WScript.Echo "INFO: SAP execute processing finished, ALV output screen/button detected"
            WaitForPostExecuteReady = True
            Err.Clear
            EchoSessionContext "ALV_READY_CONTEXT"
            Err.Clear
            Exit Function
         End If
         If Not readyLogged Then
            WScript.Echo "INFO: SAP session ready after execute, waiting for save/export action availability"
            readyLogged = True
         End If
      End If
      WScript.Sleep 1000
      waited = waited + 1000
      If waited > 0 And waited Mod 60000 = 0 Then
         WScript.Echo "INFO: still waiting for SAP execute result, waitedMs=" & CStr(waited)
         EchoSessionContext "WAIT_CONTEXT"
      End If
   Loop
   EchoSessionContext "ERROR_CONTEXT"
   EmitError "SAP execute did not finish before timeoutMs=" & CStr(timeoutMs)
End Function

Function PressSaveAfterReady(timeoutMs)
   PressSaveAfterReady = False
   If Not WaitForAnyEnabledObject("save button", Array("wnd[0]/tbar[1]/btn[14]", "wnd[0]/tbar[1]/btn[11]"), timeoutMs) Then
      EmitError "save button not enabled after SAP execute processing"
      EchoSessionContext "ERROR_CONTEXT"
      Exit Function
   End If
   PressSaveAfterReady = PressButtonByCandidates("save", Array("wnd[0]/tbar[1]/btn[14]", "wnd[0]/tbar[1]/btn[11]"))
End Function

Function WaitForSaveComplete(timeoutMs)
   Dim waited
   On Error Resume Next
   WaitForSaveComplete = False
   waited = 0
   WScript.Echo "INFO: wait for SAP save processing, timeoutMs=" & CStr(timeoutMs)
   Do While waited <= timeoutMs
      If WaitForSessionReady(1000) Then
         Err.Clear
         statusType = session.findById("wnd[0]/sbar").MessageType
         statusText = session.findById("wnd[0]/sbar").Text
         If Err.Number = 0 Then
            If statusText <> "" Then WScript.Echo "INFO: save status type=" & statusType & ", text=" & statusText
            If statusType = "E" Or statusType = "A" Then
               EmitError "SAP save rejected - " & statusText
               Err.Clear
               Exit Function
            End If
         Else
            Err.Clear
         End If
         WaitForSaveComplete = True
         Exit Function
      End If
      WScript.Sleep 1000
      waited = waited + 1000
      If waited > 0 And waited Mod 60000 = 0 Then
         WScript.Echo "INFO: still waiting for SAP save completion, waitedMs=" & CStr(waited)
         EchoSessionContext "WAIT_CONTEXT"
      End If
   Loop
   EmitError "SAP save did not finish before timeoutMs=" & CStr(timeoutMs)
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
   If Err.Number <> 0 Then
      EmitError "create FileSystemObject failed - " & Err.Description
      Err.Clear
      Exit Function
   End If
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
   If Err.Number <> 0 Then
      EmitError "create export folder failed: " & folderPath & " - " & Err.Description
      Err.Clear
      Exit Function
   End If
   EnsureFolderExists = fso.FolderExists(folderPath)
   Err.Clear
End Function

Function DeleteFileIfExists(filePath)
   Dim fso
   On Error Resume Next
   DeleteFileIfExists = False
   Err.Clear
   Set fso = CreateObject("Scripting.FileSystemObject")
   If Err.Number <> 0 Then
      EmitError "create FileSystemObject failed before ALV export cleanup - " & Err.Description
      Err.Clear
      Exit Function
   End If
   If fso.FileExists(filePath) Then
      Err.Clear
      fso.DeleteFile filePath, True
      If Err.Number <> 0 Then
         EmitError "delete existing ALV export file failed: " & filePath & " - " & Err.Description
         Err.Clear
         Exit Function
      End If
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

Function ExportedWorkbookIsOpen(filePath)
   Dim excelApp, wb, i, targetPath, workbookPath, wmi, procs, proc, cmd
   On Error Resume Next
   ExportedWorkbookIsOpen = False
   If Trim(CStr(filePath)) = "" Then Exit Function
   targetPath = LCase(Replace(CStr(filePath), "/", "\"))

   Err.Clear
   Set excelApp = GetObject(, "Excel.Application")
   If Err.Number = 0 And IsObject(excelApp) Then
      For i = excelApp.Workbooks.Count To 1 Step -1
         Err.Clear
         Set wb = excelApp.Workbooks.Item(CInt(i))
         If Err.Number = 0 And IsObject(wb) Then
            workbookPath = LCase(Replace(CStr(wb.FullName), "/", "\"))
            If workbookPath = targetPath Then
               ExportedWorkbookIsOpen = True
               Err.Clear
               Exit Function
            End If
         End If
         Err.Clear
      Next
   End If

   Err.Clear
   Set wmi = GetObject("winmgmts:\\.\root\cimv2")
   If Err.Number = 0 And IsObject(wmi) Then
      Set procs = wmi.ExecQuery("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='EXCEL.EXE'")
      If Err.Number = 0 Then
         For Each proc In procs
            cmd = ""
            If Not IsNull(proc.CommandLine) Then cmd = LCase(Replace(CStr(proc.CommandLine), "/", "\"))
            If InStr(cmd, targetPath) > 0 Then
               ExportedWorkbookIsOpen = True
               Err.Clear
               Exit Function
            End If
            Err.Clear
         Next
      End If
   End If
   Err.Clear
End Function

Function CloseExportedExcelWorkbook(filePath)
   Dim excelApp, wb, i, targetPath, workbookPath, closedCount, waited
   Dim wmi, procs, proc, cmd, terminatedCount
   On Error Resume Next
   CloseExportedExcelWorkbook = False
   If Trim(CStr(filePath)) = "" Then Exit Function
   targetPath = LCase(Replace(CStr(filePath), "/", "\"))
   waited = 0

   Do While waited <= 30000
      closedCount = 0
      terminatedCount = 0
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

      Err.Clear
      Set wmi = GetObject("winmgmts:\\.\root\cimv2")
      If Err.Number = 0 And IsObject(wmi) Then
         Set procs = wmi.ExecQuery("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='EXCEL.EXE'")
         If Err.Number = 0 Then
            For Each proc In procs
               cmd = ""
               If Not IsNull(proc.CommandLine) Then cmd = LCase(Replace(CStr(proc.CommandLine), "/", "\"))
               If InStr(cmd, targetPath) > 0 Then
                  proc.Terminate()
                  terminatedCount = terminatedCount + 1
               End If
               Err.Clear
            Next
         End If
      End If

      If terminatedCount > 0 Then WScript.Echo "INFO: terminated exported Excel process count=" & CStr(terminatedCount)
      WScript.Sleep 500

      If Not ExportedWorkbookIsOpen(filePath) Then
         Err.Clear
         If IsObject(excelApp) And excelApp.Workbooks.Count = 0 Then
            excelApp.Quit
            WScript.Echo "INFO: quit Excel after exported workbook close"
         End If
         WScript.Echo "INFO: exported Excel workbook closed or was not open=" & CStr(filePath)
         CloseExportedExcelWorkbook = True
         Err.Clear
         Exit Function
      End If

      WScript.Sleep 500
      waited = waited + 1000
   Loop

   If Not ExportedWorkbookIsOpen(filePath) Then
      WScript.Echo "INFO: exported Excel workbook closed after final check=" & CStr(filePath)
      CloseExportedExcelWorkbook = True
   Else
      WScript.Echo "WARN: exported Excel workbook still appears open after cleanup timeout=" & CStr(filePath)
   End If
   Err.Clear
End Function

Sub ScheduleExcelCloseHelper(targetPath)
   Dim fso, sh, helperDir, helperPath, ts, commandLine, targetName
   On Error Resume Next
   If Trim(CStr(targetPath)) = "" Then Exit Sub
   Set fso = CreateObject("Scripting.FileSystemObject")
   Set sh = CreateObject("WScript.Shell")
   targetName = fso.GetFileName(CStr(targetPath))
   helperDir = fso.BuildPath(fso.GetSpecialFolder(2), "sap_rpa_excel_close")
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
   ts.WriteLine "      CreateObject(""Scripting.FileSystemObject"").DeleteFile WScript.ScriptFullName, True"
   ts.WriteLine "      WScript.Quit 0"
   ts.WriteLine "    End If"
   ts.WriteLine "  End If"
   ts.WriteLine "  Err.Clear"
   ts.WriteLine "  WScript.Sleep 500"
   ts.WriteLine "  waited = waited + 500"
   ts.WriteLine "Loop"
   ts.WriteLine "CreateObject(""Scripting.FileSystemObject"").DeleteFile WScript.ScriptFullName, True"
   ts.WriteLine "WScript.Quit 0"
   ts.Close
   commandLine = """" & WScript.FullName & """ //B //Nologo """ & helperPath & """ """ & CStr(targetPath) & """"
   sh.Run commandLine, 0, False
   If Err.Number <> 0 Then
      WScript.Echo "WARN: failed to start delayed Excel close helper - " & Err.Description
      Err.Clear
   End If
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

Function ExportAlvIfConfigured(exportDir, exportFilename, timeoutMs)
   On Error Resume Next
   ExportAlvIfConfigured = False
   alvExportReady = False
   alvOutputFile = ""
   exportDir = Trim(CStr(exportDir))
   exportFilename = Trim(CStr(exportFilename))
   WScript.Echo "INFO: ALV export target dir=" & exportDir & ", filename=" & exportFilename
   If exportDir = "" Or exportFilename = "" Then
      FailAndQuit "ALV export target is empty for ZFI072A", 8
      Exit Function
   End If

   If Not EnsureFolderExists(exportDir) Then
      FailAndQuit "ALV export folder could not be prepared: " & exportDir, 8
      Exit Function
   End If
   WScript.Echo "INFO: ALV export folder ready=" & exportDir
   alvOutputFile = CombinePath(exportDir, exportFilename)
   If Not DeleteFileIfExists(alvOutputFile) Then
      FailAndQuit "ALV export target could not be cleared before export: " & alvOutputFile, 8
      Exit Function
   End If

   WScript.Echo "INFO: wait for ALV export button before press"
   If Not WaitForAnyObject("ALV export button", Array("wnd[0]/tbar[1]/btn[43]"), timeoutMs) Then
      EchoSessionContext "ERROR_CONTEXT"
      FailAndQuit "ALV export button not found before timeout", 8
      Exit Function
   End If

   Err.Clear
   session.findById("wnd[0]/tbar[1]/btn[43]").press
   If Err.Number <> 0 Then
      operationError = Err.Description
      Err.Clear
      FailAndQuit "press ALV export button failed - " & operationError, 8
      Exit Function
   End If
   WScript.Echo "INFO: pressed ALV export button"

   If Not WaitForObject("wnd[1]", 10000) Then
      EchoSessionContext "ERROR_CONTEXT"
      FailAndQuit "ALV export dialog did not open", 8
      Exit Function
   End If

   If Not ObjectExists("wnd[1]/usr/ctxtDY_PATH") Then
      Err.Clear
      session.findById("wnd[1]/tbar[0]/btn[0]").press
      If Err.Number <> 0 Then
         operationError = Err.Description
         Err.Clear
         FailAndQuit "confirm ALV export format dialog failed - " & operationError, 8
         Exit Function
      End If
      WScript.Echo "INFO: confirmed ALV export format dialog"
   End If

   If Not WaitForObject("wnd[1]/usr/ctxtDY_PATH", 10000) Then
      EchoSessionContext "ERROR_CONTEXT"
      FailAndQuit "ALV export path field did not appear", 8
      Exit Function
   End If

   Err.Clear
   session.findById("wnd[1]/usr/ctxtDY_PATH").Text = exportDir
   session.findById("wnd[1]/usr/ctxtDY_FILENAME").Text = exportFilename
   session.findById("wnd[1]/usr/ctxtDY_FILENAME").caretPosition = Len(exportFilename)
   session.findById("wnd[1]/tbar[0]/btn[0]").press
   If Err.Number <> 0 Then
      operationError = Err.Description
      Err.Clear
      FailAndQuit "submit ALV export file path failed - " & operationError, 8
      Exit Function
   End If
   WScript.Echo "INFO: submitted ALV export file=" & alvOutputFile

   ConfirmExportOverwriteIfPresent
   If Not WaitForFileReady(alvOutputFile, timeoutMs) Then
      Err.Clear
      statusType = session.findById("wnd[0]/sbar").MessageType
      statusText = session.findById("wnd[0]/sbar").Text
      If Err.Number = 0 And IsNoDataStatusText(statusText) Then
         WScript.Echo "WARN: ALV export produced no file; SAP status indicates no data"
         Err.Clear
      Else
         Err.Clear
      End If
      FailAndQuit "ALV export file was not created before timeout: " & alvOutputFile, 8
      Exit Function
   End If

   WScript.Echo "INFO: ALV export file ready=" & alvOutputFile
   If Not CloseExportedExcelWorkbook(alvOutputFile) Then
      FailAndQuit "ALV export workbook could not be closed safely: " & alvOutputFile, 8
      Exit Function
   End If
   WScript.Echo "OUTPUT_FILE=" & alvOutputFile
   alvExportReady = True
   ExportAlvIfConfigured = True
End Function

Function SessionIsUsable(candidate)
   Dim busyNow
   SessionIsUsable = False
   If Not IsObject(candidate) Then Exit Function
   Err.Clear
   busyNow = candidate.Busy
   If Err.Number = 0 And CBool(busyNow) Then
      Err.Clear
      Exit Function
   End If
   Err.Clear
   Err.Clear
   If Trim(CStr(candidate.Info.User)) = "" Then
      Err.Clear
      Exit Function
   End If
   If Trim(CStr(targetSystem)) <> "" Then
      If UCase(Trim(CStr(candidate.Info.SystemName))) <> UCase(Trim(CStr(targetSystem))) Then
         Err.Clear
         Exit Function
      End If
   End If
   If Trim(CStr(targetClient)) <> "" Then
      If Trim(CStr(candidate.Info.Client)) <> Trim(CStr(targetClient)) Then
         Err.Clear
         Exit Function
      End If
   End If
   If Trim(CStr(targetUser)) <> "" Then
      If UCase(Trim(CStr(candidate.Info.User))) <> UCase(Trim(CStr(targetUser))) Then
         Err.Clear
         Exit Function
      End If
   End If
   Err.Clear
   Dim okcd
   Set okcd = candidate.findById("wnd[0]/tbar[0]/okcd")
   If Err.Number = 0 And IsObject(okcd) Then SessionIsUsable = True
   Err.Clear
End Function

Function SafeSessionValue(candidate, valueName)
   On Error Resume Next
   SafeSessionValue = ""
   Err.Clear
   Select Case valueName
      Case "User"
         SafeSessionValue = CStr(candidate.Info.User)
      Case "Transaction"
         SafeSessionValue = CStr(candidate.Info.Transaction)
      Case "Program"
         SafeSessionValue = CStr(candidate.Info.Program)
      Case "ScreenNumber"
         SafeSessionValue = CStr(candidate.Info.ScreenNumber)
      Case "SystemName"
         SafeSessionValue = CStr(candidate.Info.SystemName)
      Case "Client"
         SafeSessionValue = CStr(candidate.Info.Client)
   End Select
   If Err.Number <> 0 Then SafeSessionValue = "<error: " & Err.Description & ">"
   Err.Clear
End Function

Function SafeObjectText(candidate, id, propertyName)
   Dim obj
   On Error Resume Next
   SafeObjectText = ""
   Err.Clear
   Set obj = candidate.findById(id)
   If Err.Number = 0 And IsObject(obj) Then
      Select Case propertyName
         Case "Text"
            SafeObjectText = CStr(obj.Text)
         Case "MessageType"
            SafeObjectText = CStr(obj.MessageType)
      End Select
   ElseIf Err.Number <> 0 Then
      SafeObjectText = "<not found: " & id & ">"
   End If
   Err.Clear
End Function

Function SafeSessionObjectEnabled(id)
   Dim obj, enabledValue
   On Error Resume Next
   SafeSessionObjectEnabled = "missing"
   If Not IsObject(session) Then Exit Function
   Err.Clear
   Set obj = session.findById(CStr(id))
   If Err.Number <> 0 Or Not IsObject(obj) Then
      Err.Clear
      Exit Function
   End If
   Err.Clear
   enabledValue = obj.Enabled
   If Err.Number <> 0 Then
      SafeSessionObjectEnabled = "exists"
   ElseIf CBool(enabledValue) Then
      SafeSessionObjectEnabled = "enabled"
   Else
      SafeSessionObjectEnabled = "disabled"
   End If
   Err.Clear
End Function

Function CandidateHasObject(candidate, id)
   Dim obj
   CandidateHasObject = False
   Err.Clear
   Set obj = candidate.findById(id)
   If Err.Number = 0 And IsObject(obj) Then CandidateHasObject = True
   Err.Clear
End Function

Sub EchoSessionContext(prefix)
   On Error Resume Next
   If Not IsObject(session) Then
      WScript.Echo prefix & ": no active session object"
      Exit Sub
   End If
   WScript.Echo prefix & ": begin"
   WScript.Echo prefix & ": transaction=" & SafeSessionValue(session, "Transaction")
   WScript.Echo prefix & ": title=" & SafeObjectText(session, "wnd[0]", "Text")
   WScript.Echo prefix & ": statusType=" & SafeObjectText(session, "wnd[0]/sbar", "MessageType")
   WScript.Echo prefix & ": statusText=" & SafeObjectText(session, "wnd[0]/sbar", "Text")
   WScript.Echo prefix & ": program=" & SafeSessionValue(session, "Program")
   WScript.Echo prefix & ": screen=" & SafeSessionValue(session, "ScreenNumber")
   WScript.Echo prefix & ": btn43=" & SafeSessionObjectEnabled("wnd[0]/tbar[1]/btn[43]")
   WScript.Echo prefix & ": btn14=" & SafeSessionObjectEnabled("wnd[0]/tbar[1]/btn[14]")
   WScript.Echo prefix & ": btn11=" & SafeSessionObjectEnabled("wnd[0]/tbar[1]/btn[11]")
End Sub

Sub EmitSapGuiDiagnostics(reason)
   Dim diagSapGuiAuto, diagApplication, diagConnection, diagSession
   Dim diagConnIndex, diagSessIndex, connectionCount, sessionCount, busyText, okcdText
   WScript.Echo "SAP_DIAG: " & reason
   Err.Clear
   Set diagSapGuiAuto = GetObject("SAPGUI")
   If Err.Number <> 0 Or Not IsObject(diagSapGuiAuto) Then
      WScript.Echo "SAP_DIAG: SAPGUI object not available - " & Err.Description
      Err.Clear
      Exit Sub
   End If
   Err.Clear
   Set diagApplication = diagSapGuiAuto.GetScriptingEngine
   If Err.Number <> 0 Or Not IsObject(diagApplication) Then
      WScript.Echo "SAP_DIAG: scripting engine not available - " & Err.Description
      Err.Clear
      Exit Sub
   End If
   Err.Clear
   connectionCount = diagApplication.Children.Count
   If Err.Number <> 0 Then
      WScript.Echo "SAP_DIAG: cannot read connection count - " & Err.Description
      Err.Clear
      Exit Sub
   End If
   WScript.Echo "SAP_DIAG: connections=" & CStr(connectionCount)
   For diagConnIndex = 0 To connectionCount - 1
      Err.Clear
      Set diagConnection = diagApplication.Children.Item(CInt(diagConnIndex))
      If Err.Number <> 0 Or Not IsObject(diagConnection) Then
         WScript.Echo "SAP_DIAG: connection[" & CStr(diagConnIndex) & "] unavailable - " & Err.Description
         Err.Clear
      Else
         Err.Clear
         sessionCount = diagConnection.Children.Count
         If Err.Number <> 0 Then
            WScript.Echo "SAP_DIAG: connection[" & CStr(diagConnIndex) & "] sessions unavailable - " & Err.Description
            Err.Clear
         Else
            WScript.Echo "SAP_DIAG: connection[" & CStr(diagConnIndex) & "] sessions=" & CStr(sessionCount)
            For diagSessIndex = 0 To sessionCount - 1
               Err.Clear
               Set diagSession = diagConnection.Children.Item(CInt(diagSessIndex))
               If Err.Number <> 0 Or Not IsObject(diagSession) Then
                  WScript.Echo "SAP_DIAG: session[" & CStr(diagConnIndex) & "." & CStr(diagSessIndex) & "] unavailable - " & Err.Description
                  Err.Clear
               Else
                  busyText = ""
                  Err.Clear
                  busyText = BoolText(diagSession.Busy)
                  If Err.Number <> 0 Then busyText = "<error: " & Err.Description & ">"
                  Err.Clear
                  okcdText = BoolText(CandidateHasObject(diagSession, "wnd[0]/tbar[0]/okcd"))
                  WScript.Echo "SAP_DIAG: session[" & CStr(diagConnIndex) & "." & CStr(diagSessIndex) & _
                     "] system=" & SafeSessionValue(diagSession, "SystemName") & _
                     ", client=" & SafeSessionValue(diagSession, "Client") & _
                     ", user=" & SafeSessionValue(diagSession, "User") & _
                     ", transaction=" & SafeSessionValue(diagSession, "Transaction") & _
                     ", title=" & SafeObjectText(diagSession, "wnd[0]", "Text") & _
                     ", statusType=" & SafeObjectText(diagSession, "wnd[0]/sbar", "MessageType") & _
                     ", statusText=" & SafeObjectText(diagSession, "wnd[0]/sbar", "Text") & _
                     ", program=" & SafeSessionValue(diagSession, "Program") & _
                     ", screen=" & SafeSessionValue(diagSession, "ScreenNumber") & _
                     ", busy=" & busyText & _
                     ", okcd=" & okcdText & _
                     ", usable=" & BoolText(SessionIsUsable(diagSession))
               End If
            Next
         End If
      End If
   Next
End Sub

Function CloseSapSession()
   Dim closeTry, exitTry, waitClose
   CloseSapSession = False
   WScript.Echo "INFO: cleanup enter, send /nex if SAP session is still open"
   If Not IsObject(session) Then
      WScript.Echo "WARN: no SAP session object to close"
      Exit Function
   End If
   If Not WaitForSessionReady(8000) Then WScript.Echo "WARN: SAP session still busy before /nex close"
   For closeTry = 1 To 3
      If Not ObjectExists("wnd[1]") Then Exit For
      Err.Clear
      session.findById("wnd[1]").sendVKey 12
      If Err.Number = 0 Then
         WScript.Echo "INFO: closed modal window before /nex"
         WScript.Sleep 500
      Else
         WScript.Echo "WARN: failed to close modal window before /nex - " & Err.Description
      End If
      Err.Clear
   Next
   For exitTry = 1 To 2
      Err.Clear
      session.findById("wnd[0]/tbar[0]/okcd").Text = "/nex"
      session.findById("wnd[0]").sendVKey 0
      If Err.Number = 0 Then
         WScript.Echo "INFO: sent /nex to close SAP session"
      Else
         WScript.Echo "WARN: failed to send /nex - " & Err.Description
         Err.Clear
         Exit For
      End If
      Err.Clear
      ConfirmSapExitModal
      For waitClose = 1 To 20
         WScript.Sleep 500
         If Not ObjectExists("wnd[0]/tbar[0]/okcd") Then
            WScript.Echo "INFO: SAP session closed after /nex"
            CloseSapSession = True
            Exit Function
         End If
         ConfirmSapExitModal
      Next
       If exitTry < 2 Then WScript.Echo "WARN: SAP session still open after /nex, retrying"
   Next
   If ObjectExists("wnd[0]/tbar[0]/okcd") Then WScript.Echo "WARN: SAP session still appears open after /nex"
End Function

Sub ConfirmSapExitModal()
   Dim confirmTry, obj
   For confirmTry = 1 To 5
      WScript.Sleep 300
      If Not ObjectExists("wnd[1]") Then Exit For
      Err.Clear
      Set obj = session.findById("wnd[1]/usr/btnSPOP-OPTION1")
      If Err.Number = 0 And IsObject(obj) Then
         obj.press
         WScript.Echo "INFO: confirmed SAP exit popup via OPTION1"
      Else
         Err.Clear
         Set obj = session.findById("wnd[1]/tbar[0]/btn[0]")
         If Err.Number = 0 And IsObject(obj) Then
            obj.press
            WScript.Echo "INFO: confirmed SAP exit popup via toolbar OK"
         Else
            Err.Clear
            session.findById("wnd[1]").sendVKey 0
            If Err.Number = 0 Then
               WScript.Echo "INFO: confirmed SAP exit popup via Enter"
            Else
               WScript.Echo "WARN: failed to confirm SAP exit popup - " & Err.Description
            End If
         End If
      End If
      Err.Clear
   Next
End Sub

Sub QuitWithCleanup(exitCode)
   sapCloseOk = CloseSapSession()
   Err.Clear
   HardQuit exitCode
End Sub

Function FillPlantMultipleSelection(value)
   Dim clipboardText, firstPlant, plantCount, multipleButton
   FillPlantMultipleSelection = False
   firstPlant = FirstCsvValue(value)
   If firstPlant = "" Then
      EmitError "no plant value"
      Exit Function
   End If

   plantCount = CsvCount(value)
   If plantCount <= 1 Then
      FillPlantMultipleSelection = SetTextByCandidates("s_werks-low", firstPlant, Array("wnd[0]/usr/ctxtS_WERKS-LOW", "wnd[0]/usr/txtS_WERKS-LOW"))
      Exit Function
   End If

   WScript.Echo "INFO: start s_werks multi plants, count=" & CStr(plantCount)
   clipboardText = CsvToClipboardText(value)
   If Not SetClipboardText(clipboardText) Then
      EmitError "failed to set clipboard for plant list"
      Exit Function
   End If
   WScript.Echo "INFO: clipboard ready for s_werks"

   If Not SetTextByCandidates("s_werks-low required seed", firstPlant, Array("wnd[0]/usr/ctxtS_WERKS-LOW", "wnd[0]/usr/txtS_WERKS-LOW")) Then
      EmitError "set required s_werks seed before multiple selection failed"
      Exit Function
   End If

   Err.Clear
   Set multipleButton = session.findById("wnd[0]/usr/btn%_S_WERKS_%_APP_%-VALU_PUSH")
   If Err.Number <> 0 Then
      EmitError "S_WERKS multiple selection button not found - " & Err.Description
      Err.Clear
      Exit Function
   End If
   multipleButton.press
   If Err.Number <> 0 Then
      EmitError "open S_WERKS multiple selection failed - " & Err.Description
      Err.Clear
      Exit Function
   End If
   If Not WaitForObject("wnd[1]", 3000) Then
      EmitError "S_WERKS multiple selection window did not open"
      Err.Clear
      Exit Function
   End If
   WScript.Echo "INFO: opened s_werks multiple selection"

   Err.Clear
   session.findById("wnd[1]/tbar[0]/btn[16]").press
   If Err.Number <> 0 Then
      EmitError "clear s_werks multiple selection failed - " & Err.Description
      Err.Clear
      Exit Function
   End If
   WScript.Echo "INFO: cleared s_werks multiple selection"

   Err.Clear
   session.findById("wnd[1]/tbar[0]/btn[24]").press
   If Err.Number <> 0 Then
      EmitError "paste s_werks plant list failed - " & Err.Description
      Err.Clear
      Exit Function
   End If
   WScript.Echo "INFO: pasted s_werks plant list"
   WScript.Sleep 500

   Err.Clear
   session.findById("wnd[1]/tbar[0]/btn[8]").press
   If Err.Number <> 0 Then
      EmitError "confirm s_werks multiple selection failed - " & Err.Description
      Err.Clear
      Exit Function
   End If
   WScript.Echo "INFO: confirmed s_werks multiple selection"
   WScript.Sleep 500
   If ObjectExists("wnd[1]") Then
      EmitError "S_WERKS multiple selection window still open after confirm"
      Err.Clear
      Exit Function
   End If
   Err.Clear

   WScript.Echo "INFO: set s_werks multi plants=" & Replace(value, ",", "|")
   FillPlantMultipleSelection = True
End Function

Function SetTextByCandidates(label, value, ids)
   Dim id, obj
   SetTextByCandidates = False
   If value = "" Then
      WScript.Echo "INFO: skip empty " & label
      SetTextByCandidates = True
      Exit Function
   End If

   For Each id In ids
      Err.Clear
      Set obj = session.findById(CStr(id))
      If Err.Number = 0 And IsObject(obj) Then
         obj.Text = CStr(value)
         If Err.Number = 0 Then
            WScript.Echo "INFO: set " & label & "=" & value & " via " & id
            SetTextByCandidates = True
            Err.Clear
            Exit Function
         End If
      End If
      Err.Clear
   Next

   EmitError "SAP field not found for " & label & ", value=" & value
   EchoSessionContext "ERROR_CONTEXT"
End Function

Sub SetCheckboxIfExists(id, selectedValue)
   Dim obj
   Err.Clear
   Set obj = session.findById(id)
   If Err.Number = 0 And IsObject(obj) Then
      obj.Selected = selectedValue
      WScript.Echo "INFO: set checkbox " & id & "=" & CStr(selectedValue)
   End If
   Err.Clear
End Sub

Function RequireObject(label, id)
   Dim obj
   RequireObject = False
   Err.Clear
   Set obj = session.findById(id)
   If Err.Number = 0 And IsObject(obj) Then
      WScript.Echo "INFO: ready " & label & " via " & id
      RequireObject = True
   Else
      EmitError "required SAP field not ready for " & label & " via " & id & " - " & Err.Description
      EchoSessionContext "ERROR_CONTEXT"
   End If
   Err.Clear
End Function

Function RequireObjectByCandidates(label, ids)
   RequireObjectByCandidates = False
   If WaitForAnyObject(label, ids, 8000) Then
      RequireObjectByCandidates = True
   Else
      EmitError "required SAP field not ready for " & label
      EchoSessionContext "ERROR_CONTEXT"
   End If
   Err.Clear
End Function

For retries = 1 To maxRetries
   Err.Clear
   Set SapGuiAuto = GetObject("SAPGUI")
   If Err.Number = 0 Then
      Set application = SapGuiAuto.GetScriptingEngine
      If Err.Number = 0 And IsObject(application) And application.Children.Count > 0 Then
         For connIndex = 0 To application.Children.Count - 1
            Err.Clear
            Set connection = application.Children.Item(CInt(connIndex))
            If Err.Number = 0 And IsObject(connection) And connection.Children.Count > 0 Then
               For sessIndex = 0 To connection.Children.Count - 1
                  Err.Clear
                  Set session = connection.Children.Item(CInt(sessIndex))
                  If Err.Number = 0 And SessionIsUsable(session) Then
                     Exit For
                  End If
               Next
               If SessionIsUsable(session) Then Exit For
            End If
         Next
         If IsObject(session) And SessionIsUsable(session) Then
            WScript.Echo "INFO: using SAP session system=" & session.Info.SystemName & ", client=" & session.Info.Client & ", user=" & session.Info.User & ", transaction=" & session.Info.Transaction
            Exit For
         End If
      End If
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

If Not IsObject(session) Or Not SessionIsUsable(session) Then
   EmitSapGuiDiagnostics "no logged-in usable SAP session after adaptive wait"
   EmitError "logged-in SAP GUI session not ready after adaptive wait"
   WScript.Quit 2
End If
If Not ObjectExists("wnd[0]/tbar[0]/okcd") Then
   EmitError "SAP command field is not ready. Check local SAP connection configuration."
   WScript.Quit 7
End If

WScript.Echo "INFO: transaction=" & tcode
WScript.Echo "INFO: ZFI072A script version=20260727-strict-alv-11"
WScript.Echo "INFO: year=" & yearValue
WScript.Echo "INFO: week=" & weekValue
If plantsCsv <> "" Then WScript.Echo "INFO: plants=" & plantsCsv
If factoryGroup <> "" Then WScript.Echo "INFO: factoryGroup=" & factoryGroup

Err.Clear
WScript.Echo "INFO: open transaction by /n" & tcode
session.findById("wnd[0]/tbar[0]/okcd").Text = "/n" & tcode
session.findById("wnd[0]").sendVKey 0
If Err.Number <> 0 Then
   EmitError "open transaction failed - " & Err.Description
   QuitWithCleanup 3
End If
If Not WaitForSessionReady(8000) Then WScript.Echo "WARN: SAP session still busy after /n" & tcode & " wait"
WScript.Sleep 500

If Not RequireObjectByCandidates("p_gjahr", Array("wnd[0]/usr/txtP_GJAHR", "wnd[0]/usr/ctxtP_GJAHR")) Then QuitWithCleanup 4
If Not RequireObjectByCandidates("p_week", Array("wnd[0]/usr/txtP_WEEK", "wnd[0]/usr/ctxtP_WEEK")) Then QuitWithCleanup 4
If Not RequireObjectByCandidates("s_werks-low", Array("wnd[0]/usr/ctxtS_WERKS-LOW", "wnd[0]/usr/txtS_WERKS-LOW")) Then QuitWithCleanup 4

Err.Clear
statusType = session.findById("wnd[0]/sbar").MessageType
statusText = session.findById("wnd[0]/sbar").Text
If Err.Number = 0 And statusText <> "" Then WScript.Echo "INFO: sap status type=" & statusType & ", text=" & statusText
If Err.Number = 0 And (statusType = "E" Or statusType = "A") Then
   WScript.Echo "STATUS_TYPE=" & statusType
   WScript.Echo "STATUS_TEXT=" & statusText
   EmitError "SAP rejected transaction " & tcode & " - " & statusText
   QuitWithCleanup 6
End If

' === SAP operation block ===
setOk = SetTextByCandidates("p_gjahr", CStr(yearValue), Array("wnd[0]/usr/txtP_GJAHR", "wnd[0]/usr/ctxtP_GJAHR"))
If Not setOk Then QuitWithCleanup 9

setOk = SetTextByCandidates("p_week", CStr(weekValue), Array("wnd[0]/usr/txtP_WEEK", "wnd[0]/usr/ctxtP_WEEK"))
If Not setOk Then QuitWithCleanup 9

setOk = FillPlantMultipleSelection(plantsCsv)
If Not setOk Then QuitWithCleanup 9

SetCheckboxIfExists "wnd[0]/usr/chkP_SEL", False

Err.Clear
session.findById("wnd[0]/tbar[1]/btn[8]").press
If Err.Number <> 0 Then
   operationError = Err.Description
   Err.Clear
   statusType = session.findById("wnd[0]/sbar").MessageType
   statusText = session.findById("wnd[0]/sbar").Text
   If Err.Number = 0 Then
      WScript.Echo "STATUS_TYPE=" & statusType
      WScript.Echo "STATUS_TEXT=" & statusText
   End If
   Err.Clear
   EmitError "SAP execute failed - " & operationError
   QuitWithCleanup 8
End If
Err.Clear
WScript.Echo "INFO: pressed execute, waiting before save"

If Not WaitForPostExecuteReady(longRunTimeoutMs) Then
   EchoSessionContext "ERROR_CONTEXT"
   If Not fatalError Then FailAndQuit "SAP execute did not reach save/export ready state", 8
   WScript.Quit fatalExitCode
End If
StopIfFatal "after WaitForPostExecuteReady"
If fatalError Then WScript.Quit fatalExitCode
If noDataResult Then
   WScript.Echo "INFO: skip save/export because SAP returned no ALV data"
Else
   WScript.Echo "INFO: start ALV export before SAP save"
   alvResult = ExportAlvIfConfigured(alvExportDir, alvExportFilename, exportTimeoutMs)
   StopIfFatal "after ExportAlvIfConfigured"
   If fatalError Then WScript.Quit fatalExitCode
   If Not alvResult Then
      If Not fatalError Then FailAndQuit "ALV export returned false", 8
      WScript.Quit fatalExitCode
   End If
   If Not alvExportReady Then
      FailAndQuit "ALV export did not reach ready marker", 8
      StopIfFatal "after missing ALV ready marker"
      WScript.Quit fatalExitCode
   End If
   WScript.Echo "INFO: ALV export completed; skip SAP save for ZFI072A report output"
End If

If Not noDataResult Then
   If Not alvExportReady Then
      FailAndQuit "ALV export did not reach ready marker before final success", 8
      StopIfFatal "before final success missing ALV marker"
      WScript.Quit fatalExitCode
   End If
   If Not FileExistsAndNotEmpty(alvOutputFile) Then
      FailAndQuit "ALV export file is missing or empty before final success: " & alvOutputFile, 8
      StopIfFatal "before final success missing ALV file"
      WScript.Quit fatalExitCode
   End If
End If

If fatalError Then WScript.Quit fatalExitCode

Err.Clear
statusType = session.findById("wnd[0]/sbar").MessageType
statusText = session.findById("wnd[0]/sbar").Text
If noDataResult Then
   statusType = "W"
   statusText = NoDataStatusText()
   WScript.Echo "STATUS_TYPE=" & statusType
   WScript.Echo "STATUS_TEXT=" & statusText
ElseIf Err.Number = 0 Then
   If statusType = "E" Or statusType = "A" Then
      FailAndQuit "SAP final status error - " & statusText, 8
      StopIfFatal "after SAP final status error"
      WScript.Quit fatalExitCode
   End If
   If Trim(CStr(statusText)) = "" Then
      statusType = "S"
      statusText = DoneStatusText()
   End If
   WScript.Echo "STATUS_TYPE=" & statusType
   WScript.Echo "STATUS_TEXT=" & statusText
Else
   FailAndQuit "SAP final status could not be read after save - " & Err.Description, 8
   StopIfFatal "after SAP final status read failure"
   WScript.Quit fatalExitCode
End If
Err.Clear

If fatalError Then WScript.Quit fatalExitCode

If shouldCloseSapAtEnd Then
   sapCloseOk = CloseSapSession()
   If Not sapCloseOk Then
      WScript.Echo "WARN: SAP GUI cleanup did not confirm /nex close"
   End If
Else
   WScript.Echo "INFO: skip /nex inside batch child; parent run will cleanup SAP after all plants"
End If
WScript.Echo "INFO: transaction script executed"
WScript.Quit 0
