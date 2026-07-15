' @tcode=ZFI057
' @name=ZFI057 value split
' @params=plants,businessAreas,period,weekEnd
' @dateRule=LAST_FULL_WEEK_BY_SYSTEM_DATE_WITH_CROSS_MONTH_SPLIT
' @factoryRule=business area maps to plant through portal/ ZTSD001; material list comes from ZFI019NL and ZFI_SPLIT upstream data
'
' Standardized for SapWebLauncher. Source is ASCII/WSH safe.

On Error Resume Next

Dim tcode, plantsCsv, businessAreasCsv, factoryGroup, materialsCsv
Dim field1Name, field1Value, field2Name, field2Value
Dim yearValue, weekValue, periodValue, weekEndValue
Dim plantValue, businessAreaValue, materialText, materialCount
Dim SapGuiAuto, application, connection, session
Dim retries, sleepMs, statusType, statusText
Dim runCount, i
Dim kadkyLow(2), kadkyHigh(2), kadatLow(2), kadatHigh(2)

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

plantValue = FirstCsvValue(plantsCsv)
businessAreaValue = FirstCsvValue(businessAreasCsv)
If plantValue = "" And businessAreaValue <> "" Then plantValue = PlantFromBusinessArea(businessAreaValue)
If plantValue = "" Then Fail "ZFI057 requires plant from {PLANTS} or resolvable business area from {BUSINESS_AREAS}", 5

materialText = ResolveMaterialText()
materialCount = CountLines(materialText)
If materialCount <= 0 Then
   Fail "ZFI057 requires material list supplied by the ZFI057 workflow after upstream ZFI019NL/ZFI_SPLIT collection.", 5
End If

ResolveZfi057DateWindows

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

Function ResolveMaterialText()
   Dim source
   source = materialsCsv
   If Trim(source) = "" And LooksLikeMaterialField(field1Name) Then source = field1Value
   If Trim(source) = "" And LooksLikeMaterialField(field2Name) Then source = field2Value
   If Trim(source) = "" And field1Name = "" And Trim(field1Value) <> "" Then source = field1Value
   If Trim(source) = "" And field2Name = "" And Trim(field2Value) <> "" Then source = field2Value
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

Sub ResolveZfi057DateWindows()
   Dim parsedStart, parsedEnd, defaultStart, defaultEnd
   Dim firstOfStartMonth, firstOfEndMonth, prevMonthStart, startMonthEnd
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
      kadatLow(1) = FormatSapDate(firstOfStartMonth)
      kadatHigh(1) = FormatSapDate(parsedEnd)
   Else
      runCount = 2
      prevMonthStart = DateSerial(Year(DateAdd("m", -1, parsedStart)), Month(DateAdd("m", -1, parsedStart)), 1)
      startMonthEnd = DateAdd("d", -1, firstOfEndMonth)
      kadkyLow(1) = FormatSapDate(prevMonthStart)
      kadkyHigh(1) = FormatSapDate(startMonthEnd)
      kadatLow(1) = FormatSapDate(DateAdd("d", 1, prevMonthStart))
      kadatHigh(1) = FormatSapDate(startMonthEnd)
      kadkyLow(2) = FormatSapDate(firstOfEndMonth)
      kadkyHigh(2) = FormatSapDate(parsedEnd)
      kadatLow(2) = FormatSapDate(firstOfEndMonth)
      kadatHigh(2) = FormatSapDate(parsedEnd)
   End If
End Sub

Function PlantFromBusinessArea(area)
   Select Case Trim(CStr(area))
      Case "2900": PlantFromBusinessArea = "1024"
      Case "9200": PlantFromBusinessArea = "1032"
      Case "2800": PlantFromBusinessArea = "1022"
      Case "3960": PlantFromBusinessArea = "6041"
      Case "2910": PlantFromBusinessArea = "103C"
      Case "3400": PlantFromBusinessArea = "1031"
      Case "2920": PlantFromBusinessArea = "1033"
      Case "5100": PlantFromBusinessArea = "1035"
      Case "2790": PlantFromBusinessArea = "1036"
      Case Else: PlantFromBusinessArea = ""
   End Select
End Function

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

Sub PressButton(id, label, timeoutMs)
   Err.Clear
   session.findById(id).press
   If Err.Number <> 0 Then Fail label & " failed - " & Err.Description, 8
   WScript.Echo "INFO: pressed " & label
   Err.Clear
   WaitReady timeoutMs
   CheckSapStatus label
End Sub

Sub SetClipboardText(value)
   Dim html
   Err.Clear
   Set html = CreateObject("htmlfile")
   html.ParentWindow.ClipboardData.SetData "text", CStr(value)
   If Err.Number <> 0 Then Fail "set clipboard material list failed - " & Err.Description, 8
   WScript.Echo "INFO: material clipboard prepared count=" & materialCount
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

Sub PasteMaterialSelection()
   SetClipboardText materialText
   PressButton "wnd[0]/usr/btn%_S_MATNR_%_APP_%-VALU_PUSH", "open S_MATNR multiple selection", 8000
   PressButton "wnd[1]/tbar[0]/btn[24]", "paste S_MATNR material list", 8000
   PressButton "wnd[1]/tbar[0]/btn[8]", "confirm S_MATNR material list", 8000
End Sub

Sub RunZfi057Window(index)
   WScript.Echo "INFO: zfi057 input group #" & index
   WScript.Echo "INFO: query ZTSD001 where GSBER=" & businessAreaValue & "; resolved WERKS=" & plantValue
   WScript.Echo "INFO: query ZFI_SPLIT fields=BUKRS,WERKS,MATNR,BEGDA,ENDDA,MTART; WERKS=" & plantValue & "; BEGDA<=" & kadkyHigh(index) & "; ENDDA>=" & kadkyLow(index) & "; MTART=*"
   WScript.Echo "INFO: upstream material count=" & materialCount
   OpenTransaction
   SetField "werks-low", "wnd[0]/usr/ctxtS_WERKS-LOW", plantValue
   SetField "kadky-low", "wnd[0]/usr/ctxtS_KADKY-LOW", kadkyLow(index)
   SetField "kadky-high", "wnd[0]/usr/ctxtS_KADKY-HIGH", kadkyHigh(index)
   SetField "mtart-low", "wnd[0]/usr/ctxtS_MTART-LOW", "*"
   SetField "kadat-low", "wnd[0]/usr/ctxtS_KADAT-LOW", kadatLow(index)
   SetField "kadat-high", "wnd[0]/usr/ctxtS_KADAT-HIGH", kadatHigh(index)
   PasteMaterialSelection
   PressButton "wnd[0]/tbar[1]/btn[8]", "execute ZFI057 group #" & index, 1200000
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
WScript.Echo "INFO: period=" & periodValue
WScript.Echo "INFO: weekEnd=" & weekEndValue
WScript.Echo "INFO: plants=" & plantsCsv
WScript.Echo "INFO: plant=" & plantValue
WScript.Echo "INFO: businessAreas=" & businessAreasCsv
WScript.Echo "INFO: businessArea=" & businessAreaValue
If factoryGroup <> "" Then WScript.Echo "INFO: factoryGroup=" & factoryGroup
WScript.Echo "INFO: zfi057 date window count=" & runCount

For i = 1 To runCount
   RunZfi057Window i
Next

CheckSapStatus "finish"
WScript.Echo "INFO: transaction script executed"
WScript.Quit 0
