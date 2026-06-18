' @tcode=ZFI072A
' @name=ZFI072A purchase price monthly report
' @params=year,week,plants
' @dateRule=LAST_WEEK_ISO
' @factoryRule=plants are supplied by launcher/API
'
' Generated from SAP GUI Recorder output. Review SAP operation block before production use.

On Error Resume Next

Dim tcode, plantsCsv, businessAreasCsv, factoryGroup, unresolvedPlantsToken, unresolvedOkCodeToken
Dim targetSystem, targetClient, targetUser, unresolvedSapSystemToken, unresolvedSapClientToken, unresolvedSapUserToken
Dim targetDate, yearValue, weekValue, pageYear, pageWeek, periodValue, weekEndValue
Dim plantValue, setOk
Dim SapGuiAuto, application, connection, session, connIndex, sessIndex
Dim retries, maxRetries, sleepMs, statusType, statusText, operationError
Dim longRunTimeoutMs, saveTimeoutMs, saveButtonTimeoutMs, sapCloseOk

tcode = "{OK_CODE}"
targetSystem = "{SAP_SYSTEM}"
targetClient = "{SAP_CLIENT}"
targetUser = "{SAP_USER}"
plantsCsv = "{PLANTS}"
businessAreasCsv = "{BUSINESS_AREAS}"
factoryGroup = "{FACTORY_GROUP}"
pageYear = "{YEAR}"
pageWeek = "{WEEK}"
periodValue = "{PERIOD}"
weekEndValue = "{WEEK_END}"
maxRetries = 100
longRunTimeoutMs = 3600000
If CsvContains(plantsCsv, "9301") Then longRunTimeoutMs = 7200000
saveTimeoutMs = 600000
saveButtonTimeoutMs = 120000
unresolvedPlantsToken = "{" & "PLANTS" & "}"
unresolvedOkCodeToken = "{" & "OK_CODE" & "}"
unresolvedSapSystemToken = "{" & "SAP_SYSTEM" & "}"
unresolvedSapClientToken = "{" & "SAP_CLIENT" & "}"
unresolvedSapUserToken = "{" & "SAP_USER" & "}"

If Trim(CStr(tcode)) = "" Or Trim(CStr(tcode)) = unresolvedOkCodeToken Then tcode = "ZFI072A"
If Trim(CStr(targetSystem)) = unresolvedSapSystemToken Then targetSystem = ""
If Trim(CStr(targetClient)) = unresolvedSapClientToken Then targetClient = ""
If Trim(CStr(targetUser)) = unresolvedSapUserToken Then targetUser = ""
If UCase(Trim(CStr(tcode))) <> "ZFI072A" Then
   EmitError "ZFI072A script refuses non-ZFI072A tcode=" & CStr(tcode)
   WScript.Quit 10
End If

Sub EmitError(message)
   If Trim(CStr(statusType)) = "" Then statusType = "E"
   If Trim(CStr(statusText)) = "" Then statusText = CStr(message)
   WScript.Echo "STATUS_TYPE=" & statusType
   WScript.Echo "STATUS_TEXT=" & statusText
   WScript.Echo "ERROR=" & message
   WScript.Echo "ERROR: " & message
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

Function WaitForSessionReady(timeoutMs)
   Dim waited, busyNow
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
   ObjectIsEnabled = False
   Err.Clear
   Set obj = session.findById(CStr(id))
   If Err.Number = 0 And IsObject(obj) Then
      enabledValue = True
      Err.Clear
      enabledValue = obj.Enabled
      If Err.Number <> 0 Then enabledValue = True
      ObjectIsEnabled = CBool(enabledValue)
   End If
   Err.Clear
End Function

Function WaitForAnyEnabledObject(label, ids, timeoutMs)
   Dim waited, id
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

Function WaitForPostExecuteReady(timeoutMs)
   Dim waited, readyLogged
   WaitForPostExecuteReady = False
   waited = 0
   readyLogged = False
   WScript.Echo "INFO: wait for SAP execute processing, timeoutMs=" & CStr(timeoutMs)
   Do While waited <= timeoutMs
      If WaitForSessionReady(1000) Then
         Err.Clear
         statusType = session.findById("wnd[0]/sbar").MessageType
         statusText = session.findById("wnd[0]/sbar").Text
         If Err.Number = 0 Then
            If statusText <> "" Then WScript.Echo "INFO: post execute status type=" & statusType & ", text=" & statusText
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
         If Not readyLogged Then
            WScript.Echo "INFO: SAP session ready after execute, waiting for save action availability"
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

Function CandidateHasObject(candidate, id)
   Dim obj
   CandidateHasObject = False
   Err.Clear
   Set obj = candidate.findById(id)
   If Err.Number = 0 And IsObject(obj) Then CandidateHasObject = True
   Err.Clear
End Function

Sub EchoSessionContext(prefix)
   If Not IsObject(session) Then
      WScript.Echo prefix & ": no active session object"
      Exit Sub
   End If
   WScript.Echo prefix & ": transaction=" & SafeSessionValue(session, "Transaction") & _
      ", title=" & SafeObjectText(session, "wnd[0]", "Text") & _
      ", statusType=" & SafeObjectText(session, "wnd[0]/sbar", "MessageType") & _
      ", statusText=" & SafeObjectText(session, "wnd[0]/sbar", "Text") & _
      ", program=" & SafeSessionValue(session, "Program") & _
      ", screen=" & SafeSessionValue(session, "ScreenNumber")
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
   WScript.Quit exitCode
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

If Not WaitForPostExecuteReady(longRunTimeoutMs) Then QuitWithCleanup 8
If Not PressSaveAfterReady(saveButtonTimeoutMs) Then QuitWithCleanup 8
If Not WaitForSaveComplete(saveTimeoutMs) Then QuitWithCleanup 8

Err.Clear
statusType = session.findById("wnd[0]/sbar").MessageType
statusText = session.findById("wnd[0]/sbar").Text
If Err.Number = 0 Then
   If Trim(CStr(statusText)) = "" Then
      statusType = "S"
      statusText = DoneStatusText()
   End If
   WScript.Echo "STATUS_TYPE=" & statusType
   WScript.Echo "STATUS_TEXT=" & statusText
Else
   statusType = "S"
   statusText = DoneStatusText()
   WScript.Echo "STATUS_TYPE=" & statusType
   WScript.Echo "STATUS_TEXT=" & statusText
End If
Err.Clear

WScript.Echo "OUTPUT_FILE="
sapCloseOk = CloseSapSession()
If Not sapCloseOk Then
   WScript.Echo "WARN: SAP GUI cleanup did not confirm /nex close"
End If
WScript.Echo "INFO: transaction script executed"
WScript.Quit 0
