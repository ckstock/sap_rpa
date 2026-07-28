' @tcode=ZFI019NL
' @name=ZFI019NL business area receipt export
' @params=businessAreas
' @dateRule=LAST_FULL_WEEK_BY_SYSTEM_DATE
' @factoryRule=single business area supplied by launcher/API
'
' Standardized for SapWebLauncher. Source is ASCII/WSH safe.

On Error Resume Next

Dim tcode, businessAreasCsv, factoryGroup
Dim yearValue, weekValue, periodValue, weekEndValue, dateLowValue, dateHighValue
Dim businessAreaValue
Dim SapGuiAuto, application, connection, session
Dim retries, sleepMs, statusType, statusText
Dim unresolvedOkCodeToken, unresolvedAreasToken, unresolvedAlvExportDirToken, unresolvedAlvExportFilenameToken
Dim materialCsv, materialCount
Dim alvExportDir, alvExportFilename, scriptDir, exportTimeoutMs, alvHelperLoaded

tcode = "{OK_CODE}"
businessAreasCsv = "{BUSINESS_AREAS}"
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
unresolvedAreasToken = "{" & "BUSINESS_AREAS" & "}"
unresolvedAlvExportDirToken = "{" & "ALV_EXPORT_DIR" & "}"
unresolvedAlvExportFilenameToken = "{" & "ALV_EXPORT_FILENAME" & "}"

If Trim(CStr(tcode)) = "" Or Trim(CStr(tcode)) = unresolvedOkCodeToken Then tcode = "ZFI019NL"
If UCase(Trim(CStr(tcode))) <> "ZFI019NL" Then Fail "ZFI019NL script refuses tcode=" & CStr(tcode), 10
If Trim(CStr(businessAreasCsv)) = unresolvedAreasToken Then businessAreasCsv = ""
If IsPlaceholder(yearValue, "YEAR") Then yearValue = ""
If IsPlaceholder(weekValue, "WEEK") Then weekValue = ""
If IsPlaceholder(periodValue, "PERIOD") Then periodValue = ""
If IsPlaceholder(weekEndValue, "WEEK_END") Then weekEndValue = ""
If Trim(CStr(alvExportDir)) = unresolvedAlvExportDirToken Then alvExportDir = ""
If Trim(CStr(alvExportFilename)) = unresolvedAlvExportFilenameToken Then alvExportFilename = ""
If IsPlaceholder(scriptDir, "SCRIPT_DIR") Then scriptDir = ""

businessAreaValue = FirstCsvValue(businessAreasCsv)
If businessAreaValue = "" Then Fail "ZFI019NL requires one business area from {BUSINESS_AREAS}", 5

ResolveDates

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

Sub ExportAlvBeforeSave(label, suffix)
   Dim exportFilename
   If Not LoadAlvExportHelper() Then Fail "ALV export helper could not be loaded", 8
   exportFilename = alvExportFilename
   If Trim(CStr(suffix)) <> "" Then exportFilename = AlvBuildExportFilename(alvExportFilename, suffix)
   If Not AlvExportIfConfigured(session, alvExportDir, exportFilename, exportTimeoutMs) Then Fail "ALV export returned false before " & label, 8
   WScript.Echo "INFO: ALV export completed before " & label
End Sub

Function SapDate(value)
   Dim v
   v = Trim(CStr(value))
   If v = "" Then
      SapDate = ""
   Else
      SapDate = Replace(v, "-", ".")
   End If
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

Sub ResolveDates()
   Dim parsedEnd, parsedStart, defaultEnd, defaultStart
   defaultStart = DateAdd("d", -7, WeekStart(Date))
   defaultEnd = DateAdd("d", 6, defaultStart)
   If Trim(CStr(yearValue)) = "" Then yearValue = Year(defaultStart)
   If Trim(CStr(weekValue)) = "" Then weekValue = DatePart("ww", defaultStart, vbMonday, vbFirstFourDays)
   parsedStart = ParseDateOrEmpty(periodValue)
   parsedEnd = ParseDateOrEmpty(weekEndValue)
   If IsEmpty(parsedStart) Then parsedStart = defaultStart
   If IsEmpty(parsedEnd) Then parsedEnd = defaultEnd
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

Sub PressExecute()
   PressToolbarButton "wnd[0]/tbar[1]/btn[8]", "execute"
End Sub

Sub SelectAllGrid()
   Err.Clear
   session.findById("wnd[0]/usr/cntlGRID1/shellcont/shell").setCurrentCell -1, ""
   session.findById("wnd[0]/usr/cntlGRID1/shellcont/shell").selectAll
   If Err.Number <> 0 Then Fail "select all grid failed - " & Err.Description, 8
   WScript.Echo "INFO: selected all grid rows"
   Err.Clear
End Sub

Sub SelectGridColumn(columnName)
   Err.Clear
   session.findById("wnd[0]/usr/cntlGRID1/shellcont/shell").selectColumn CStr(columnName)
   If Err.Number <> 0 Then Fail "select grid column " & columnName & " failed - " & Err.Description, 8
   WScript.Echo "INFO: selected grid column " & columnName
   Err.Clear
End Sub

Function IsSpecialBusinessArea(value)
   Dim normalized
   normalized = UCase(Trim(CStr(value)))
   IsSpecialBusinessArea = (normalized = "0162" Or normalized = "7700" Or normalized = "7800" Or normalized = "7600" Or normalized = "1070" Or normalized = "0500" Or normalized = "7900")
End Function

Function StartsWith800(value)
   StartsWith800 = (Left(Trim(CStr(value)), 3) = "800")
End Function

Function GridColumnExists(grid, columnName)
   Dim probe
   Err.Clear
   probe = grid.GetCellValue(0, CStr(columnName))
   GridColumnExists = (Err.Number = 0)
   Err.Clear
End Function

Function ResolveGridColumn(grid, rowCount, candidatesCsv)
   Dim parts, item
   ResolveGridColumn = ""
   parts = Split(CStr(candidatesCsv), ",")
   If CLng(rowCount) <= 0 Then
      ResolveGridColumn = Trim(CStr(parts(0)))
      Exit Function
   End If
   For Each item In parts
      item = Trim(CStr(item))
      If item <> "" And GridColumnExists(grid, item) Then
         ResolveGridColumn = item
         Exit Function
      End If
   Next
End Function

Function TryGridCell(grid, rowIndex, columnName)
   Err.Clear
   TryGridCell = Trim(CStr(grid.GetCellValue(CInt(rowIndex), CStr(columnName))))
   If Err.Number <> 0 Then
      Err.Clear
      TryGridCell = ""
   End If
End Function

Sub EmitMaterialListFromGrid()
   Dim grid, rowCount, materialColumn, productColumn
   Dim dict, rowIndex, materialValue, productValue, keepRow, key
   materialCsv = ""
   materialCount = 0

   Err.Clear
   Set grid = session.findById("wnd[0]/usr/cntlGRID1/shellcont/shell")
   If Err.Number <> 0 Or Not IsObject(grid) Then
      WScript.Echo "WARN: material extraction grid not found - " & Err.Description
      Err.Clear
      Exit Sub
   End If

   Err.Clear
   rowCount = CLng(grid.RowCount)
   If Err.Number <> 0 Then
      WScript.Echo "WARN: material extraction rowCount unavailable - " & Err.Description
      Err.Clear
      Exit Sub
   End If

   materialColumn = ResolveGridColumn(grid, rowCount, "MATNR,MATNR1,RMATNR,IMATNR")
   productColumn = ResolveGridColumn(grid, rowCount, "SMATNR,PMATNR")
   WScript.Echo "INFO: zfi019nl material extraction rowCount=" & rowCount & ", materialColumn=" & materialColumn & ", productColumn=" & productColumn

   If materialColumn = "" Then
      WScript.Echo "WARN: material extraction skipped because MATNR column was not found"
      Exit Sub
   End If
   If IsSpecialBusinessArea(businessAreaValue) And productColumn = "" Then
      WScript.Echo "WARN: special business area requires SMATNR/product column for 800* filtering, but column was not found"
      Exit Sub
   End If

   Set dict = CreateObject("Scripting.Dictionary")
   For rowIndex = 0 To CLng(rowCount) - 1
      materialValue = TryGridCell(grid, rowIndex, materialColumn)
      If materialValue <> "" Then
         keepRow = True
         If IsSpecialBusinessArea(businessAreaValue) Then
            productValue = TryGridCell(grid, rowIndex, productColumn)
            keepRow = StartsWith800(productValue)
         End If
         If keepRow And Not dict.Exists(materialValue) Then dict.Add materialValue, materialValue
      End If
   Next

   materialCount = dict.Count
   If materialCount > 0 Then materialCsv = Join(dict.Keys, ",")
   WScript.Echo "MATERIAL_COUNT=" & materialCount
   If materialCsv <> "" Then WScript.Echo "MATERIALS_CSV=" & materialCsv
   For Each key In dict.Keys
      WScript.Echo "MATERIAL=" & CStr(key)
   Next

   If IsSpecialBusinessArea(businessAreaValue) Then
      WScript.Echo "INFO: zfi_split fields=BUKRS,WERKS,MATNR,BEGDA,ENDDA,MTART require backend RFC/NCo; GUI step emitted ZFI019NL grid materials"
   End If
End Sub

Sub SetRadioIfExists(id)
   Err.Clear
   If ObjectExists(id) Then
      session.findById(id).select
      session.findById(id).setFocus
      WScript.Echo "INFO: selected radio " & id
   Else
      Fail "radio not found " & id, 8
   End If
   Err.Clear
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
If businessAreaValue <> "" Then WScript.Echo "INFO: businessArea=" & businessAreaValue
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
SetField "budat-low", "wnd[0]/usr/ctxtS_BUDAT-LOW", dateLowValue
SetField "budat-high", "wnd[0]/usr/ctxtS_BUDAT-HIGH", dateHighValue
SetField "gsber-low", "wnd[0]/usr/ctxtS_GSBER-LOW", businessAreaValue
PressExecute
WaitReady 600000
EmitMaterialListFromGrid
SelectAllGrid
ExportAlvBeforeSave "save result", ""
PressToolbarButton "wnd[0]/tbar[1]/btn[16]", "save/export result"

CheckSapStatus "finish"
WScript.Echo "INFO: transaction script executed"
WScript.Quit 0
