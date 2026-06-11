' @tcode=ZFI072A
' @name=采购价月表
' @params=year,week,plants
' @dateRule=LAST_WEEK_ISO
' @factoryRule=先四个集采工厂，再其他工厂
'
' Generated from SAP GUI Recorder output. Review SAP 操作区 before production use.

On Error Resume Next

Dim tcode, plantsCsv, businessAreasCsv, factoryGroup, unresolvedPlantsToken
Dim targetDate, yearValue, weekValue, pageYear, pageWeek, periodValue, weekEndValue
Dim plantValue, setOk
Dim SapGuiAuto, application, connection, session
Dim retries, maxRetries, sleepMs, statusType, statusText, operationError

tcode = "{OK_CODE}"
plantsCsv = "{PLANTS}"
businessAreasCsv = "{BUSINESS_AREAS}"
factoryGroup = "{FACTORY_GROUP}"
pageYear = "{YEAR}"
pageWeek = "{WEEK}"
periodValue = "{PERIOD}"
weekEndValue = "{WEEK_END}"
maxRetries = 100
unresolvedPlantsToken = "{" & "PLANTS" & "}"

Sub EmitError(message)
   WScript.Echo "ERROR=" & message
   WScript.Echo "ERROR: " & message
End Sub

If Trim(CStr(plantsCsv)) = "" Or Trim(CStr(plantsCsv)) = unresolvedPlantsToken Then
   EmitError "ZFI072A requires plants from launcher input/API selection"
   WScript.Quit 5
End If

targetDate = DateAdd("d", -7, Date)
yearValue = Year(targetDate)
weekValue = DatePart("ww", targetDate, vbMonday, vbFirstFourDays)
If pageYear <> "" Then yearValue = pageYear
If pageWeek <> "" Then weekValue = pageWeek

Function ObjectExists(id)
   Dim obj
   Err.Clear
   Set obj = session.findById(id)
   ObjectExists = (Err.Number = 0 And IsObject(obj))
   Err.Clear
End Function

Function FirstCsvValue(value)
   Dim parts, item
   value = Replace(value, ";", ",")
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
   parts = Split(value, ",")
   count = 0
   For Each item In parts
      item = Trim(CStr(item))
      If item <> "" Then count = count + 1
   Next
   CsvCount = count
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
   End If
   Err.Clear
End Function

For retries = 1 To maxRetries
   Err.Clear
   Set SapGuiAuto = GetObject("SAPGUI")
   If Err.Number = 0 Then
      Set application = SapGuiAuto.GetScriptingEngine
      If Err.Number = 0 And IsObject(application) And application.Children.Count > 0 Then
         Set connection = application.Children(0)
         If Err.Number = 0 And IsObject(connection) And connection.Children.Count > 0 Then
            Set session = connection.Children(0)
            If Err.Number = 0 And IsObject(session) And ObjectExists("wnd[0]/tbar[0]/okcd") Then
               Exit For
            End If
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

If Not IsObject(session) Then
   EmitError "SAP GUI session not ready after adaptive wait"
   WScript.Quit 2
End If
If Not ObjectExists("wnd[0]/tbar[0]/okcd") Then
   EmitError "SAP command field is not ready. Check local SAP connection configuration."
   WScript.Quit 7
End If

WScript.Echo "INFO: transaction=" & tcode
WScript.Echo "INFO: year=" & yearValue
WScript.Echo "INFO: week=" & weekValue
If plantsCsv <> "" Then WScript.Echo "INFO: plants=" & plantsCsv
If businessAreasCsv <> "" Then WScript.Echo "INFO: businessAreas=" & businessAreasCsv
If factoryGroup <> "" Then WScript.Echo "INFO: factoryGroup=" & factoryGroup

Err.Clear
WScript.Echo "INFO: open transaction by /n" & tcode
session.findById("wnd[0]/tbar[0]/okcd").Text = "/n" & tcode
session.findById("wnd[0]").sendVKey 0
If Err.Number <> 0 Then
   EmitError "open transaction failed - " & Err.Description
   WScript.Quit 3
End If
WScript.Sleep 1000

If Not RequireObject("p_gjahr", "wnd[0]/usr/txtP_GJAHR") Then WScript.Quit 4
If Not RequireObject("p_week", "wnd[0]/usr/txtP_WEEK") Then WScript.Quit 4
If Not RequireObject("s_werks-low", "wnd[0]/usr/ctxtS_WERKS-LOW") Then WScript.Quit 4

Err.Clear
statusType = session.findById("wnd[0]/sbar").MessageType
statusText = session.findById("wnd[0]/sbar").Text
If Err.Number = 0 And statusText <> "" Then WScript.Echo "INFO: sap status type=" & statusType & ", text=" & statusText
If Err.Number = 0 And (statusType = "E" Or statusType = "A") Then
   WScript.Echo "STATUS_TYPE=" & statusType
   WScript.Echo "STATUS_TEXT=" & statusText
   EmitError "SAP rejected transaction " & tcode & " - " & statusText
   WScript.Quit 6
End If

' === SAP 操作区 ===
setOk = SetTextByCandidates("p_gjahr", CStr(yearValue), Array("wnd[0]/usr/txtP_GJAHR", "wnd[0]/usr/ctxtP_GJAHR"))
If Not setOk Then WScript.Quit 9

setOk = SetTextByCandidates("p_week", CStr(weekValue), Array("wnd[0]/usr/txtP_WEEK", "wnd[0]/usr/ctxtP_WEEK"))
If Not setOk Then WScript.Quit 9

setOk = FillPlantMultipleSelection(plantsCsv)
If Not setOk Then WScript.Quit 9

SetCheckboxIfExists "wnd[0]/usr/chkP_SEL", False

session.findById("wnd[0]/tbar[1]/btn[8]").press
session.findById("wnd[0]/tbar[1]/btn[14]").press
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
   EmitError "SAP operation failed - " & operationError
   WScript.Quit 8
End If

Err.Clear
statusType = session.findById("wnd[0]/sbar").MessageType
statusText = session.findById("wnd[0]/sbar").Text
If Err.Number = 0 Then
   WScript.Echo "STATUS_TYPE=" & statusType
   WScript.Echo "STATUS_TEXT=" & statusText
End If
Err.Clear

WScript.Echo "OUTPUT_FILE="
WScript.Echo "INFO: transaction script executed"
WScript.Quit 0
