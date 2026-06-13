Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text

Module Program
    Private Structure Options
        Public ImagePath As String
        Public RootX As Double
        Public RootY As Double
        Public WireAngle As Double
        Public Threshold As Double
        Public X0 As Integer
        Public Y0 As Integer
        Public X1 As Integer
        Public Y1 As Integer
        Public OutputPath As String
        Public JsonPath As String
    End Structure

    Private Structure PointD
        Public X As Double
        Public Y As Double

        Public Sub New(xValue As Double, yValue As Double)
            X = xValue
            Y = yValue
        End Sub
    End Structure

    Private Structure MeasurementResult
        Public TopPoint As PointD
        Public BottomPoint As PointD
        Public Midpoint As PointD
        Public AxisAngleDeg As Double
        Public BetaDeg As Double
        Public ArcPixels As Integer
        Public IncludedPixels As List(Of Point)
    End Structure

    Sub Main(args As String())
        Try
            Dim opts = ParseArgs(args)
            If String.IsNullOrWhiteSpace(opts.ImagePath) OrElse opts.ImagePath = "--help" OrElse opts.ImagePath = "-h" Then
                PrintHelp()
                Return
            End If

            Dim result = MeasureArcAngle(opts)

            If String.IsNullOrWhiteSpace(opts.OutputPath) Then
                opts.OutputPath = Path.Combine(
                    Path.GetDirectoryName(opts.ImagePath),
                    Path.GetFileNameWithoutExtension(opts.ImagePath) & "_arc_angle_measured_vbnet.png"
                )
            End If

            If String.IsNullOrWhiteSpace(opts.JsonPath) Then
                opts.JsonPath = Path.Combine(
                    Path.GetDirectoryName(opts.ImagePath),
                    Path.GetFileNameWithoutExtension(opts.ImagePath) & "_arc_angle_result_vbnet.json"
                )
            End If

            DrawAnnotation(opts, result)
            WriteJson(opts, result)

            Console.WriteLine("beta_deg = " & result.BetaDeg.ToString("F3", CultureInfo.InvariantCulture))
            Console.WriteLine("annotated = " & opts.OutputPath)
            Console.WriteLine("json = " & opts.JsonPath)
        Catch ex As Exception
            Console.Error.WriteLine("Error: " & ex.Message)
            Environment.ExitCode = 1
        End Try
    End Sub

    Private Function ParseArgs(args As String()) As Options
        Dim opts As New Options With {
            .RootX = 872.0,
            .RootY = 529.3,
            .WireAngle = 1.55,
            .Threshold = 210.0,
            .X0 = 670,
            .Y0 = 60,
            .X1 = 1010,
            .Y1 = 700,
            .OutputPath = "",
            .JsonPath = ""
        }

        If args.Length = 0 Then
            opts.ImagePath = "--help"
            Return opts
        End If

        opts.ImagePath = args(0)
        Dim i = 1
        While i < args.Length
            Select Case args(i)
                Case "--root-x"
                    i += 1
                    opts.RootX = ParseDouble(args, i, "--root-x")
                Case "--root-y"
                    i += 1
                    opts.RootY = ParseDouble(args, i, "--root-y")
                Case "--wire-angle"
                    i += 1
                    opts.WireAngle = ParseDouble(args, i, "--wire-angle")
                Case "--threshold"
                    i += 1
                    opts.Threshold = ParseDouble(args, i, "--threshold")
                Case "--roi"
                    i += 1
                    Dim roi = ParseRoi(args, i)
                    opts.X0 = roi.Item1
                    opts.Y0 = roi.Item2
                    opts.X1 = roi.Item3
                    opts.Y1 = roi.Item4
                Case "--output"
                    i += 1
                    opts.OutputPath = ParseString(args, i, "--output")
                Case "--json"
                    i += 1
                    opts.JsonPath = ParseString(args, i, "--json")
                Case Else
                    Throw New ArgumentException("Unknown argument: " & args(i))
            End Select
            i += 1
        End While

        Return opts
    End Function

    Private Function ParseDouble(args As String(), index As Integer, name As String) As Double
        If index >= args.Length Then Throw New ArgumentException("Missing value for " & name)
        Return Double.Parse(args(index), CultureInfo.InvariantCulture)
    End Function

    Private Function ParseString(args As String(), index As Integer, name As String) As String
        If index >= args.Length Then Throw New ArgumentException("Missing value for " & name)
        Return args(index)
    End Function

    Private Function ParseRoi(args As String(), index As Integer) As Tuple(Of Integer, Integer, Integer, Integer)
        If index >= args.Length Then Throw New ArgumentException("Missing value for --roi")
        Dim parts = args(index).Split(","c).Select(Function(s) Integer.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray()
        If parts.Length <> 4 Then Throw New ArgumentException("--roi must use x0,y0,x1,y1")
        If parts(2) <= parts(0) OrElse parts(3) <= parts(1) Then Throw New ArgumentException("--roi must satisfy x1>x0 and y1>y0")
        Return Tuple.Create(parts(0), parts(1), parts(2), parts(3))
    End Function

    Private Sub PrintHelp()
        Console.WriteLine("CmtArcAngle")
        Console.WriteLine("Measure CMT arc angle using gray threshold and vertical extreme midpoint.")
        Console.WriteLine()
        Console.WriteLine("Usage:")
        Console.WriteLine("  CmtArcAngle.exe image.bmp [--threshold 210] [--root-x 872] [--root-y 529.3] [--wire-angle 1.55] [--roi 670,60,1010,700]")
        Console.WriteLine()
        Console.WriteLine("Outputs:")
        Console.WriteLine("  image_arc_angle_measured_vbnet.png")
        Console.WriteLine("  image_arc_angle_result_vbnet.json")
    End Sub

    Private Function MeasureArcAngle(opts As Options) As MeasurementResult
        If Not File.Exists(opts.ImagePath) Then Throw New FileNotFoundException("Image not found", opts.ImagePath)

        Using bitmap As New Bitmap(opts.ImagePath)
            Dim gray = BuildGrayArray(bitmap)
            ValidateRoi(opts, bitmap.Width, bitmap.Height)

            Dim maskHeight = opts.Y1 - opts.Y0
            Dim maskWidth = opts.X1 - opts.X0
            Dim mask(maskHeight - 1, maskWidth - 1) As Boolean

            For y = 0 To maskHeight - 1
                For x = 0 To maskWidth - 1
                    mask(y, x) = gray(opts.Y0 + y, opts.X0 + x) > opts.Threshold
                Next
            Next

            Dim components = ConnectedComponents(mask, 80)
            If components.Count = 0 Then Throw New InvalidOperationException("No arc component found. Lower threshold or adjust ROI.")

            Dim rootRoiX = opts.RootX - opts.X0
            Dim rootRoiY = opts.RootY - opts.Y0
            Dim component = SelectComponentNearRoot(components, rootRoiX, rootRoiY)

            Dim included As New List(Of Point)()
            For Each p In component
                Dim imageX = p.X + opts.X0
                Dim imageY = p.Y + opts.Y0
                Dim dx = imageX - opts.RootX
                Dim dy = imageY - opts.RootY
                Dim radius = Math.Sqrt(dx * dx + dy * dy)
                Dim angle = RadiansToDegrees(Math.Atan2(dy, dx))

                Dim inFan = ((angle < -35.0) OrElse (angle > 95.0) OrElse ((imageX < opts.RootX + 70.0) AndAlso (imageY < opts.RootY + 60.0))) AndAlso radius > 8.0
                If inFan Then included.Add(New Point(imageX, imageY))
            Next

            If included.Count < 20 Then Throw New InvalidOperationException("Too few arc pixels after filtering. Lower threshold or adjust ROI.")

            Dim minY = included.Min(Function(p) p.Y)
            Dim maxY = included.Max(Function(p) p.Y)
            Dim topXs = included.Where(Function(p) p.Y <= minY + 4).Select(Function(p) CDbl(p.X)).OrderBy(Function(v) v).ToList()
            Dim topYs = included.Where(Function(p) p.Y <= minY + 4).Select(Function(p) CDbl(p.Y)).OrderBy(Function(v) v).ToList()
            Dim bottomXs = included.Where(Function(p) p.Y >= maxY - 4).Select(Function(p) CDbl(p.X)).OrderBy(Function(v) v).ToList()
            Dim bottomYs = included.Where(Function(p) p.Y >= maxY - 4).Select(Function(p) CDbl(p.Y)).OrderBy(Function(v) v).ToList()

            Dim topPoint As New PointD(Median(topXs), Median(topYs))
            Dim bottomPoint As New PointD(Median(bottomXs), Median(bottomYs))
            Dim midpoint As New PointD((topPoint.X + bottomPoint.X) / 2.0, (topPoint.Y + bottomPoint.Y) / 2.0)

            Dim axisAngle = RadiansToDegrees(Math.Atan2(midpoint.Y - opts.RootY, midpoint.X - opts.RootX))
            Dim beta = IncludedLineAngleDeg(axisAngle, opts.WireAngle)

            Return New MeasurementResult With {
                .TopPoint = topPoint,
                .BottomPoint = bottomPoint,
                .Midpoint = midpoint,
                .AxisAngleDeg = axisAngle,
                .BetaDeg = beta,
                .ArcPixels = included.Count,
                .IncludedPixels = included
            }
        End Using
    End Function

    Private Function BuildGrayArray(bitmap As Bitmap) As Double(,)
        Dim gray(bitmap.Height - 1, bitmap.Width - 1) As Double
        For y = 0 To bitmap.Height - 1
            For x = 0 To bitmap.Width - 1
                Dim c = bitmap.GetPixel(x, y)
                gray(y, x) = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B
            Next
        Next
        Return gray
    End Function

    Private Sub ValidateRoi(opts As Options, width As Integer, height As Integer)
        If opts.X0 < 0 OrElse opts.Y0 < 0 OrElse opts.X1 > width OrElse opts.Y1 > height OrElse opts.X1 <= opts.X0 OrElse opts.Y1 <= opts.Y0 Then
            Throw New ArgumentException("ROI is outside image bounds or invalid.")
        End If
    End Sub

    Private Function ConnectedComponents(mask As Boolean(,), minArea As Integer) As List(Of List(Of Point))
        Dim height = mask.GetLength(0)
        Dim width = mask.GetLength(1)
        Dim visited(height - 1, width - 1) As Boolean
        Dim components As New List(Of List(Of Point))()
        Dim offsets = {New Point(1, 0), New Point(-1, 0), New Point(0, 1), New Point(0, -1)}

        For y = 0 To height - 1
            For x = 0 To width - 1
                If Not mask(y, x) OrElse visited(y, x) Then Continue For

                Dim queue As New Queue(Of Point)()
                Dim points As New List(Of Point)()
                queue.Enqueue(New Point(x, y))
                visited(y, x) = True

                While queue.Count > 0
                    Dim current = queue.Dequeue()
                    points.Add(current)
                    For Each offset In offsets
                        Dim nx = current.X + offset.X
                        Dim ny = current.Y + offset.Y
                        If nx >= 0 AndAlso nx < width AndAlso ny >= 0 AndAlso ny < height AndAlso mask(ny, nx) AndAlso Not visited(ny, nx) Then
                            visited(ny, nx) = True
                            queue.Enqueue(New Point(nx, ny))
                        End If
                    Next
                End While

                If points.Count >= minArea Then components.Add(points)
            Next
        Next

        Return components
    End Function

    Private Function SelectComponentNearRoot(components As List(Of List(Of Point)), rootRoiX As Double, rootRoiY As Double) As List(Of Point)
        Dim bestComponent As List(Of Point) = Nothing
        Dim bestScore = Double.PositiveInfinity

        For Each component In components
            Dim minDist2 = component.Min(Function(p)
                                             Dim dx = p.X - rootRoiX
                                             Dim dy = p.Y - rootRoiY
                                             Return dx * dx + dy * dy
                                         End Function)
            Dim score = minDist2 - 0.0002 * component.Count
            If score < bestScore Then
                bestScore = score
                bestComponent = component
            End If
        Next

        Return bestComponent
    End Function

    Private Sub DrawAnnotation(opts As Options, result As MeasurementResult)
        Using source As New Bitmap(opts.ImagePath)
            Using image As New Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb)
                Using initGraphics As Graphics = Graphics.FromImage(image)
                    initGraphics.DrawImageUnscaled(source, 0, 0)
                End Using

                Using graphics As Graphics = Graphics.FromImage(image)
                graphics.DrawRectangle(New Pen(Color.Yellow, 2), opts.X0, opts.Y0, opts.X1 - opts.X0, opts.Y1 - opts.Y0)

                Dim stepSize = Math.Max(1, result.IncludedPixels.Count \ 12000)
                For i = 0 To result.IncludedPixels.Count - 1 Step stepSize
                    Dim p = result.IncludedPixels(i)
                    image.SetPixel(p.X, p.Y, Color.FromArgb(255, 150, 0))
                Next

                DrawCircle(graphics, result.TopPoint, Pens.Cyan, 7)
                DrawCircle(graphics, result.BottomPoint, Pens.Cyan, 7)
                graphics.DrawLine(New Pen(Color.FromArgb(0, 180, 255), 2), CSng(result.TopPoint.X), CSng(result.TopPoint.Y), CSng(result.BottomPoint.X), CSng(result.BottomPoint.Y))

                DrawCircle(graphics, result.Midpoint, Pens.Magenta, 7)

                Dim root As New PointD(opts.RootX, opts.RootY)
                DrawCircle(graphics, root, Pens.Yellow, 7)
                graphics.DrawLine(Pens.Yellow, CSng(opts.RootX - 12), CSng(opts.RootY), CSng(opts.RootX + 12), CSng(opts.RootY))
                graphics.DrawLine(Pens.Yellow, CSng(opts.RootX), CSng(opts.RootY - 12), CSng(opts.RootX), CSng(opts.RootY + 12))

                graphics.DrawLine(New Pen(Color.Red, 4), CSng(opts.RootX), CSng(opts.RootY), CSng(result.Midpoint.X), CSng(result.Midpoint.Y))

                Dim wireSlope = Math.Tan(DegreesToRadians(opts.WireAngle))
                Dim xw = image.Width - 20
                Dim yw = opts.RootY + wireSlope * (xw - opts.RootX)
                graphics.DrawLine(New Pen(Color.Lime, 3), CSng(opts.RootX), CSng(opts.RootY), CSng(xw), CSng(yw))

                    graphics.DrawString(
                        "gray>" & opts.Threshold.ToString("F0", CultureInfo.InvariantCulture) & ": beta=" & result.BetaDeg.ToString("F2", CultureInfo.InvariantCulture) & " deg",
                        New Font("Arial", 14, FontStyle.Bold),
                        Brushes.Yellow,
                        30,
                        30
                    )
                End Using

                image.Save(opts.OutputPath, ImageFormat.Png)
            End Using
        End Using
    End Sub

    Private Sub DrawCircle(graphics As Graphics, point As PointD, pen As Pen, radius As Integer)
        graphics.DrawEllipse(pen, CSng(point.X - radius), CSng(point.Y - radius), radius * 2, radius * 2)
    End Sub

    Private Sub WriteJson(opts As Options, result As MeasurementResult)
        Dim sb As New StringBuilder()
        sb.AppendLine("{")
        AppendJson(sb, "image", opts.ImagePath, True)
        AppendJson(sb, "method", "gray_threshold_vertical_extreme_midpoint", True)
        AppendJson(sb, "threshold", opts.Threshold, True)
        sb.AppendLine("  ""roi"": { ""x0"": " & opts.X0 & ", ""y0"": " & opts.Y0 & ", ""x1"": " & opts.X1 & ", ""y1"": " & opts.Y1 & " },")
        sb.AppendLine("  ""wire_tip"": { ""x"": " & JsonNumber(opts.RootX) & ", ""y"": " & JsonNumber(opts.RootY) & " },")
        AppendJson(sb, "wire_angle_deg", opts.WireAngle, True)
        sb.AppendLine("  ""top_point"": { ""x"": " & JsonNumber(result.TopPoint.X) & ", ""y"": " & JsonNumber(result.TopPoint.Y) & " },")
        sb.AppendLine("  ""bottom_point"": { ""x"": " & JsonNumber(result.BottomPoint.X) & ", ""y"": " & JsonNumber(result.BottomPoint.Y) & " },")
        sb.AppendLine("  ""midpoint"": { ""x"": " & JsonNumber(result.Midpoint.X) & ", ""y"": " & JsonNumber(result.Midpoint.Y) & " },")
        AppendJson(sb, "arc_axis_angle_deg", result.AxisAngleDeg, True)
        AppendJson(sb, "beta_deg", result.BetaDeg, True)
        sb.AppendLine("  ""arc_pixels"": " & result.ArcPixels)
        sb.AppendLine("}")
        File.WriteAllText(opts.JsonPath, sb.ToString(), Encoding.UTF8)
    End Sub

    Private Sub AppendJson(sb As StringBuilder, key As String, value As String, comma As Boolean)
        sb.Append("  """).Append(EscapeJson(key)).Append(""": """).Append(EscapeJson(value)).Append("""")
        If comma Then sb.Append(",")
        sb.AppendLine()
    End Sub

    Private Sub AppendJson(sb As StringBuilder, key As String, value As Double, comma As Boolean)
        sb.Append("  """).Append(EscapeJson(key)).Append(""": ").Append(JsonNumber(value))
        If comma Then sb.Append(",")
        sb.AppendLine()
    End Sub

    Private Function EscapeJson(value As String) As String
        Return value.Replace("\", "\\").Replace("""", "\""")
    End Function

    Private Function JsonNumber(value As Double) As String
        Return value.ToString("0.###############", CultureInfo.InvariantCulture)
    End Function

    Private Function Median(values As List(Of Double)) As Double
        If values.Count = 0 Then Throw New InvalidOperationException("Cannot compute median of empty list.")
        Dim middle = values.Count \ 2
        If values.Count Mod 2 = 1 Then Return values(middle)
        Return (values(middle - 1) + values(middle)) / 2.0
    End Function

    Private Function IncludedLineAngleDeg(a As Double, b As Double) As Double
        Dim delta = Math.Abs(a - b) Mod 180.0
        If delta > 90.0 Then Return 180.0 - delta
        Return delta
    End Function

    Private Function DegreesToRadians(degrees As Double) As Double
        Return degrees * Math.PI / 180.0
    End Function

    Private Function RadiansToDegrees(radians As Double) As Double
        Return radians * 180.0 / Math.PI
    End Function
End Module
