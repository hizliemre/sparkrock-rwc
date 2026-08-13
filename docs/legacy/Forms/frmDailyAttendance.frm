VERSION 5.00
Begin VB.Form frmDailyAttendance
   Caption         =   "Daily Attendance Entry"
   ClientHeight    =   6285
   ClientLeft      =   60
   ClientTop       =   345
   ClientWidth     =   9480
   StartUpPosition =   2
   Begin VB.ComboBox cboSchool
      Height = 315 : Left = 1440 : TabIndex = 0 : Top = 240 : Width = 2535
   End
   Begin VB.ComboBox cboGrade
      Height = 315 : Left = 4320 : TabIndex = 1 : Top = 240 : Width = 1455
   End
   Begin VB.MSFlexGrid grdStudents
      Height = 4575 : Left = 120 : TabIndex = 3 : Top = 840 : Width = 9255
   End
   Begin VB.CommandButton cmdSave
      Caption = "Save Attendance" : Height = 495 : Left = 7440 : TabIndex = 4 : Top = 5640 : Width = 1815
   End
   Begin VB.CommandButton cmdPrint
      Caption = "Print Report" : Height = 495 : Left = 5880 : TabIndex = 5 : Top = 5640 : Width = 1455
   End
End
Attribute VB_Name = "frmDailyAttendance"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
Private moConn As ADODB.Connection
Private moRS   As ADODB.Recordset
Private mlSchoolID As Long
Private mdAttendDate As Date
Private Sub Form_Load()
    On Error GoTo ErrHandler
    Set moConn = New ADODB.Connection
    moConn.Open "Provider=SQLOLEDB;Data Source=" & App.Path & "\config.ini"
    LoadSchools
    mdAttendDate = Date
    Exit Sub
ErrHandler:
    MsgBox "Error: " & Err.Description
End Sub
Private Sub LoadSchools()
    Dim oRS As ADODB.Recordset
    Set oRS = moConn.Execute("SELECT SchoolID, SchoolName FROM Schools WHERE Active = 1 ORDER BY SchoolName")
    Do While Not oRS.EOF
        cboSchool.AddItem oRS("SchoolName")
        cboSchool.ItemData(cboSchool.NewIndex) = oRS("SchoolID")
        oRS.MoveNext
    Loop
End Sub
Private Sub cboSchool_Click()
    mlSchoolID = cboSchool.ItemData(cboSchool.ListIndex)
    LoadGrades
    LoadStudents
End Sub
Private Sub LoadGrades()
    cboGrade.Clear
    Dim oRS As ADODB.Recordset
    Set oRS = moConn.Execute("SELECT DISTINCT Grade FROM Students WHERE SchoolID = " & mlSchoolID & " AND Active = 1 ORDER BY Grade")
    Do While Not oRS.EOF
        cboGrade.AddItem oRS("Grade")
        oRS.MoveNext
    Loop
End Sub
Private Sub LoadStudents()
    On Error GoTo ErrHandler
    If mlSchoolID = 0 Then Exit Sub
    Dim sSQL As String
    sSQL = "EXEC sp_GetStudentsForAttendance " & mlSchoolID & ", '" & cboGrade.Text & "', '" & Format(mdAttendDate, "yyyy-mm-dd") & "'"
    Set moRS = New ADODB.Recordset
    moRS.Open sSQL, moConn, adOpenStatic, adLockOptimistic
    With grdStudents
        .Cols = 6 : .Rows = 1
        .TextMatrix(0, 0) = "Student ID"
        .TextMatrix(0, 1) = "Last Name"
        .TextMatrix(0, 2) = "First Name"
        .TextMatrix(0, 3) = "Code"
        .TextMatrix(0, 4) = "Min Late"
        .TextMatrix(0, 5) = "Notes"
        Dim i As Integer
        i = 1
        Do While Not moRS.EOF
            .Rows = i + 1
            .TextMatrix(i, 0) = moRS("StudentID")
            .TextMatrix(i, 1) = moRS("LastName")
            .TextMatrix(i, 2) = moRS("FirstName")
            .TextMatrix(i, 3) = Nz(moRS("AttendCode"), "P")
            .TextMatrix(i, 4) = Nz(moRS("MinutesLate"), "0")
            .TextMatrix(i, 5) = Nz(moRS("Notes"), "")
            i = i + 1
            moRS.MoveNext
        Loop
    End With
    Exit Sub
ErrHandler:
    MsgBox "Error loading students: " & Err.Description
End Sub
Private Sub cmdSave_Click()
    On Error GoTo ErrHandler
    Dim i As Integer
    Dim sXML As String
    sXML = "<attendance>"
    For i = 1 To grdStudents.Rows - 1
        Dim sCode As String
        sCode = Trim(grdStudents.TextMatrix(i, 3))
        If sCode = "" Then sCode = "P"
        sXML = sXML & "<r sid=""" & Trim(grdStudents.TextMatrix(i, 0)) & _
               """ code=""" & sCode & _
               """ min=""" & Val(grdStudents.TextMatrix(i, 4)) & _
               """ note=""" & Trim(grdStudents.TextMatrix(i, 5)) & """/>"
    Next i
    sXML = sXML & "</attendance>"
    moConn.Execute "EXEC sp_SaveDailyAttendance " & mlSchoolID & ", '" & _
                   Format(mdAttendDate, "yyyy-mm-dd") & "', '" & sXML & "'"
    MsgBox "Attendance saved successfully."
    Exit Sub
ErrHandler:
    MsgBox "Save failed: " & Err.Description
End Sub
Private Sub cmdPrint_Click()
    Dim oCR As Object
    Set oCR = CreateObject("CrystalRuntime.Application")
    Dim oReport As Object
    Set oReport = oCR.OpenReport(App.Path & "\Reports\DailyAttendance.rpt")
    oReport.RecordSelectionFormula = "{Attendance.SchoolID} = " & mlSchoolID & _
        " AND {Attendance.AttendDate} = Date(" & Year(mdAttendDate) & "," & _
        Month(mdAttendDate) & "," & Day(mdAttendDate) & ")"
    oReport.PrintOut False
End Sub
Private Sub Form_Unload(Cancel As Integer)
    If Not moConn Is Nothing Then
        If moConn.State = adStateOpen Then moConn.Close
    End If
    Set moConn = Nothing
    Set moRS = Nothing
End Sub
