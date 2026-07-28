' Shared ALV export helper for SapWebLauncher transaction scripts.
' The caller must pass a ready SAP GUI session plus a unique export folder/name
' injected by SapWebLauncher.

On Error Resume Next

Function AlvCombinePath(folderPath, fileName)
   If Right(CStr(folderPath), 1) = "\" Then
      AlvCombinePath = CStr(folderPath) & CStr(fileName)
   Else
      AlvCombinePath = CStr(folderPath) & "\" & CStr(fileName)
   End If
End Function

Sub AlvFail(message, code)
   WScript.Echo "STATUS_TYPE=E"
   WScript.Echo "STATUS_TEXT=" & message
   WScript.Echo "ERROR=" & message
   WScript.Echo "ERROR: " & message
   WScript.Quit code
End Sub

Function AlvObjectExists(sapSession, id)
   Dim obj
   Err.Clear
   Set obj = sapSession.findById(CStr(id))
   AlvObjectExists = (Err.Number = 0 And IsObject(obj))
   Err.Clear
End Function

Function AlvEnsureFolderExists(folderPath)
   Dim fso, parentPath
   On Error Resume Next
   AlvEnsureFolderExists = False
   folderPath = Trim(CStr(folderPath))
   If folderPath = "" Then Exit Function
   Set fso = CreateObject("Scripting.FileSystemObject")
   If fso.FolderExists(folderPath) Then
      AlvEnsureFolderExists = True
      Exit Function
   End If
   parentPath = fso.GetParentFolderName(folderPath)
   If parentPath <> "" And Not fso.FolderExists(parentPath) Then
      If Not AlvEnsureFolderExists(parentPath) Then Exit Function
   End If
   Err.Clear
   fso.CreateFolder folderPath
   AlvEnsureFolderExists = (Err.Number = 0 Or fso.FolderExists(folderPath))
   Err.Clear
End Function

Function AlvDeleteFileIfExists(filePath)
   Dim fso
   On Error Resume Next
   AlvDeleteFileIfExists = True
   Set fso = CreateObject("Scripting.FileSystemObject")
   If fso.FileExists(CStr(filePath)) Then
      Err.Clear
      fso.DeleteFile CStr(filePath), True
      AlvDeleteFileIfExists = (Err.Number = 0)
      Err.Clear
   End If
End Function

Function AlvWaitReady(sapSession, timeoutMs)
   Dim waited
   On Error Resume Next
   waited = 0
   Do While waited <= CLng(timeoutMs)
      Err.Clear
      If Not CBool(sapSession.Busy) Then
         AlvWaitReady = True
         Err.Clear
         Exit Function
      End If
      Err.Clear
      WScript.Sleep 250
      waited = waited + 250
   Loop
   AlvWaitReady = False
End Function

Function AlvWaitForObject(sapSession, id, timeoutMs)
   Dim waited
   On Error Resume Next
   waited = 0
   Do While waited <= CLng(timeoutMs)
      If AlvObjectExists(sapSession, id) Then
         AlvWaitForObject = True
         Exit Function
      End If
      WScript.Sleep 250
      waited = waited + 250
   Loop
   AlvWaitForObject = False
End Function

Function AlvFileReady(filePath)
   Dim fso, fileObj, size1, size2
   On Error Resume Next
   AlvFileReady = False
   Set fso = CreateObject("Scripting.FileSystemObject")
   If Not fso.FileExists(CStr(filePath)) Then Exit Function
   Set fileObj = fso.GetFile(CStr(filePath))
   size1 = CLng(fileObj.Size)
   If size1 <= 0 Then Exit Function
   WScript.Sleep 500
   Set fileObj = fso.GetFile(CStr(filePath))
   size2 = CLng(fileObj.Size)
   AlvFileReady = (size1 = size2 And size2 > 0)
   Err.Clear
End Function

Function AlvWaitForFileReady(filePath, timeoutMs)
   Dim waited
   On Error Resume Next
   waited = 0
   Do While waited <= CLng(timeoutMs)
      If AlvFileReady(filePath) Then
         AlvWaitForFileReady = True
         Exit Function
      End If
      WScript.Sleep 500
      waited = waited + 500
   Loop
   AlvWaitForFileReady = False
End Function

Sub AlvConfirmOverwriteIfPresent(sapSession)
   Dim confirmTry, obj
   On Error Resume Next
   For confirmTry = 1 To 5
      WScript.Sleep 300
      If Not AlvObjectExists(sapSession, "wnd[1]") Then Exit For
      Err.Clear
      Set obj = sapSession.findById("wnd[1]/usr/btnSPOP-OPTION1")
      If Err.Number = 0 And IsObject(obj) Then
         obj.press
         WScript.Echo "INFO: confirmed ALV export overwrite via OPTION1"
      Else
         Err.Clear
         Set obj = sapSession.findById("wnd[1]/tbar[0]/btn[0]")
         If Err.Number = 0 And IsObject(obj) And Not AlvObjectExists(sapSession, "wnd[1]/usr/ctxtDY_PATH") Then
            obj.press
            WScript.Echo "INFO: confirmed ALV export dialog via toolbar OK"
         End If
      End If
      Err.Clear
   Next
End Sub

Function AlvPressExportEntry(sapSession, timeoutMs)
   Dim grid
   On Error Resume Next
   AlvPressExportEntry = False

   Err.Clear
   If AlvObjectExists(sapSession, "wnd[0]/tbar[1]/btn[43]") Then
      sapSession.findById("wnd[0]/tbar[1]/btn[43]").press
      If Err.Number = 0 Then
         WScript.Echo "INFO: pressed ALV export button 43"
         AlvWaitReady sapSession, timeoutMs
         AlvPressExportEntry = True
         Exit Function
      End If
   End If

   Err.Clear
   Set grid = sapSession.findById("wnd[0]/usr/cntlGRID1/shellcont/shell")
   If Err.Number = 0 And IsObject(grid) Then
      Err.Clear
      grid.pressToolbarContextButton "&MB_EXPORT"
      If Err.Number = 0 Then
         grid.selectContextMenuItem "&XXL"
         If Err.Number = 0 Then
            WScript.Echo "INFO: pressed ALV grid context export &XXL"
            AlvWaitReady sapSession, timeoutMs
            AlvPressExportEntry = True
            Exit Function
         End If
      End If

      Err.Clear
      grid.pressToolbarButton "&XXL"
      If Err.Number = 0 Then
         WScript.Echo "INFO: pressed ALV grid toolbar &XXL"
         AlvWaitReady sapSession, timeoutMs
         AlvPressExportEntry = True
         Exit Function
      End If
   End If
   Err.Clear
End Function

Function AlvExportedWorkbookIsOpen(filePath)
   Dim targetPath, excelApp, workbookIndex, workbook, workbookPath
   Dim wmi, procs, proc, cmd
   On Error Resume Next
   AlvExportedWorkbookIsOpen = False
   targetPath = LCase(Replace(CStr(filePath), "/", "\"))

   Err.Clear
   Set excelApp = GetObject(, "Excel.Application")
   If Err.Number = 0 And IsObject(excelApp) Then
      For workbookIndex = excelApp.Workbooks.Count To 1 Step -1
         Err.Clear
         Set workbook = excelApp.Workbooks.Item(CInt(workbookIndex))
         workbookPath = ""
         If Err.Number = 0 And IsObject(workbook) Then workbookPath = LCase(Replace(CStr(workbook.FullName), "/", "\"))
         If Err.Number = 0 And workbookPath = targetPath Then
            AlvExportedWorkbookIsOpen = True
            Err.Clear
            Exit Function
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
               AlvExportedWorkbookIsOpen = True
               Err.Clear
               Exit Function
            End If
            Err.Clear
         Next
      End If
   End If
   Err.Clear
End Function

Function AlvCloseExportedExcelWorkbook(filePath)
   Dim targetPath, waited, excelApp, workbookIndex, workbook, workbookPath
   Dim wmi, procs, proc, cmd, closedCount, terminatedCount
   On Error Resume Next
   AlvCloseExportedExcelWorkbook = False
   targetPath = LCase(Replace(CStr(filePath), "/", "\"))
   waited = 0

   Do While waited <= 30000
      closedCount = 0
      terminatedCount = 0
      Err.Clear
      Set excelApp = GetObject(, "Excel.Application")
      If Err.Number = 0 And IsObject(excelApp) Then
         excelApp.DisplayAlerts = False
         For workbookIndex = excelApp.Workbooks.Count To 1 Step -1
            Err.Clear
            Set workbook = excelApp.Workbooks.Item(CInt(workbookIndex))
            workbookPath = ""
            If Err.Number = 0 And IsObject(workbook) Then workbookPath = LCase(Replace(CStr(workbook.FullName), "/", "\"))
            If Err.Number = 0 And workbookPath = targetPath Then
               workbook.Close False
               If Err.Number = 0 Then
                  closedCount = closedCount + 1
                  WScript.Echo "INFO: closed exported Excel workbook=" & CStr(filePath)
               End If
            End If
            Err.Clear
         Next
         If closedCount > 0 Then
            WScript.Sleep 500
            If excelApp.Workbooks.Count = 0 Then excelApp.Quit
            Exit Do
         End If
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
      If Not AlvExportedWorkbookIsOpen(filePath) Then
         WScript.Echo "INFO: exported Excel workbook closed or was not open=" & CStr(filePath)
         AlvCloseExportedExcelWorkbook = True
         Err.Clear
         Exit Function
      End If

      WScript.Sleep 500
      waited = waited + 1000
   Loop

   If Not AlvExportedWorkbookIsOpen(filePath) Then
      WScript.Echo "INFO: exported Excel workbook closed after final check=" & CStr(filePath)
      AlvCloseExportedExcelWorkbook = True
   Else
      WScript.Echo "WARN: exported Excel workbook still appears open after cleanup timeout=" & CStr(filePath)
   End If
   Err.Clear
End Function

Function AlvBuildExportFilename(baseFileName, suffix)
   Dim fso, stem, ext
   On Error Resume Next
   suffix = Trim(CStr(suffix))
   AlvBuildExportFilename = CStr(baseFileName)
   If suffix = "" Then Exit Function
   Set fso = CreateObject("Scripting.FileSystemObject")
   stem = fso.GetBaseName(CStr(baseFileName))
   ext = fso.GetExtensionName(CStr(baseFileName))
   If Trim(CStr(stem)) = "" Then stem = "sap_alv"
   suffix = Replace(Replace(Replace(suffix, " ", "_"), "\", "_"), "/", "_")
   If Trim(CStr(ext)) = "" Then
      AlvBuildExportFilename = stem & "_" & suffix & ".xlsx"
   Else
      AlvBuildExportFilename = stem & "_" & suffix & "." & ext
   End If
   Err.Clear
End Function

Function AlvExportIfConfigured(sapSession, exportDir, exportFilename, timeoutMs)
   Dim outputFile
   On Error Resume Next
   AlvExportIfConfigured = False
   exportDir = Trim(CStr(exportDir))
   exportFilename = Trim(CStr(exportFilename))
   WScript.Echo "INFO: ALV export target dir=" & exportDir & ", filename=" & exportFilename
   If exportDir = "" Or exportFilename = "" Then AlvFail "ALV export target is empty", 8
   If Not AlvEnsureFolderExists(exportDir) Then AlvFail "ALV export folder could not be prepared: " & exportDir, 8

   outputFile = AlvCombinePath(exportDir, exportFilename)
   If Not AlvDeleteFileIfExists(outputFile) Then AlvFail "ALV export target could not be cleared before export: " & outputFile, 8
   If Not AlvPressExportEntry(sapSession, timeoutMs) Then AlvFail "ALV export entry not found or not usable before timeout", 8

   If Not AlvWaitForObject(sapSession, "wnd[1]", 10000) Then AlvFail "ALV export dialog did not open", 8
   If Not AlvObjectExists(sapSession, "wnd[1]/usr/ctxtDY_PATH") Then
      Err.Clear
      sapSession.findById("wnd[1]/tbar[0]/btn[0]").press
      If Err.Number <> 0 Then AlvFail "confirm ALV export format dialog failed - " & Err.Description, 8
      WScript.Echo "INFO: confirmed ALV export format dialog"
   End If

   If Not AlvWaitForObject(sapSession, "wnd[1]/usr/ctxtDY_PATH", 10000) Then AlvFail "ALV export path field did not appear", 8
   Err.Clear
   sapSession.findById("wnd[1]/usr/ctxtDY_PATH").Text = exportDir
   sapSession.findById("wnd[1]/usr/ctxtDY_FILENAME").Text = exportFilename
   sapSession.findById("wnd[1]/usr/ctxtDY_FILENAME").caretPosition = Len(exportFilename)
   sapSession.findById("wnd[1]/tbar[0]/btn[0]").press
   If Err.Number <> 0 Then AlvFail "submit ALV export file path failed - " & Err.Description, 8
   WScript.Echo "INFO: submitted ALV export file=" & outputFile

   AlvConfirmOverwriteIfPresent sapSession
   If Not AlvWaitForFileReady(outputFile, timeoutMs) Then AlvFail "ALV export file was not created before timeout: " & outputFile, 8
   WScript.Echo "INFO: ALV export file ready=" & outputFile
   If Not AlvCloseExportedExcelWorkbook(outputFile) Then AlvFail "ALV export workbook could not be closed safely: " & outputFile, 8
   WScript.Echo "OUTPUT_FILE=" & outputFile
   AlvExportIfConfigured = True
End Function
