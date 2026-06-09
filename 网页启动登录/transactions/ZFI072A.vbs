' @tcode=ZFI072A
' @name=采购价月表
' @params=year,week,plants
' @dateRule=LAST_WEEK_ISO
' @factoryRule=先四个集采工厂，再其他工厂
'
' 第一版脚本用于打开事务码并输出动态参数。录制真实脚本后替换 SAP 操作区。

On Error Resume Next

Dim tcode, plantsCsv, factoryGroup
Dim targetDate, yearValue, weekValue, pageYear, pageWeek
Dim SapGuiAuto, application, connection, session
Dim retries, maxRetries, sleepMs, statusType, statusText

tcode = "{OK_CODE}"
plantsCsv = "{PLANTS}"
factoryGroup = "{FACTORY_GROUP}"
pageYear = "{YEAR}"
pageWeek = "{WEEK}"
maxRetries = 100

targetDate = DateAdd("d", -7, Date)
yearValue = Year(targetDate)
weekValue = DatePart("ww", targetDate, vbMonday, vbFirstFourDays)

Function ObjectExists(id)
   Dim obj
   Err.Clear
   Set obj = session.findById(id)
   ObjectExists = (Err.Number = 0 And IsObject(obj))
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
            If Err.Number = 0 And IsObject(session) And ObjectExists("wnd[0]/tbar[0]/okcd") And Not ObjectExists("wnd[0]/usr/pwdRSYST-BCODE") Then
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
   WScript.Echo "ERROR: SAP GUI session not ready after adaptive wait"
   WScript.Quit 2
End If
If ObjectExists("wnd[0]/usr/pwdRSYST-BCODE") Then
   WScript.Echo "ERROR: SAP login screen is still active. Check local config user/password/system/client."
   WScript.Quit 7
End If

WScript.Echo "INFO: transaction=" & tcode
WScript.Echo "INFO: year=" & yearValue
WScript.Echo "INFO: week=" & weekValue
If pageYear <> "" Then WScript.Echo "INFO: yearFromPage=" & pageYear
If pageWeek <> "" Then WScript.Echo "INFO: weekFromPage=" & pageWeek
If plantsCsv <> "" Then WScript.Echo "INFO: plants=" & plantsCsv
If factoryGroup <> "" Then WScript.Echo "INFO: factoryGroup=" & factoryGroup

Err.Clear
session.findById("wnd[0]/tbar[0]/okcd").Text = "/n" & tcode
session.findById("wnd[0]").sendVKey 0
If Err.Number <> 0 Then
   WScript.Echo "ERROR: open transaction failed - " & Err.Description
   WScript.Quit 3
End If
WScript.Sleep 1000

Err.Clear
statusType = session.findById("wnd[0]/sbar").MessageType
statusText = session.findById("wnd[0]/sbar").Text
If Err.Number = 0 And statusText <> "" Then WScript.Echo "INFO: sap status type=" & statusType & ", text=" & statusText
If Err.Number = 0 And (statusType = "E" Or statusType = "A") Then
   WScript.Echo "ERROR: SAP rejected transaction " & tcode & " - " & statusText
   WScript.Quit 6
End If

' === SAP 操作区 ===
' 录制脚本整理示例：
' session.findById("wnd[0]/usr/txtGJAHR").Text = CStr(yearValue)
' session.findById("wnd[0]/usr/txtWEEK").Text = CStr(weekValue)
' session.findById("wnd[0]/usr/ctxtWERKS").Text = "1024"

WScript.Echo "INFO: external script placeholder executed"
WScript.Echo "INFO: transaction script executed"
WScript.Quit 0
