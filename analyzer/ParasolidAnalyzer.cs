using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PK = PLMComponents.Parasolid.PK_.Unsafe;

internal static unsafe class ParasolidAnalyzer
{
    private sealed class BodyEntry
    {
        public int PartitionId;
        public string PartitionLabel;
        public string SourcePath;
        public PK.BODY_t Body;
        public PK.BODY.type_t BodyType;
        public int FaceCount;
        public int EdgeCount;
        public int VertexCount;
        public PK.BOX_t Box;
    }

    private sealed class FaceInfo
    {
        public PK.VECTOR_t Normal;
        public PK.VECTOR_t InteriorPoint;
        public bool NormalValid;
        public string NormalStatus;
        public string SurfaceClass;
        public PK.BOX_t Box;
    }

    private static readonly PK.FSTART.f_t StartCallback = Start;
    private static readonly PK.FABORT.f_t AbortCallback = Abort;
    private static readonly PK.FSTOP.f_t StopCallback = Stop;
    private static readonly PK.FMALLO.f_t AllocateCallback = Allocate;
    private static readonly PK.FMFREE.f_t FreeCallback = Free;
    private static readonly PK.FFOPRD.f_t OpenReadCallback = OpenRead;
    private static readonly PK.FFCLOS.f_t CloseCallback = Close;
    private static readonly PK.FFREAD.f_t ReadCallback = Read;
    private static readonly PK.FFSEEK.f_t SeekCallback = Seek;
    private static readonly PK.FFTELL.f_t TellCallback = Tell;
    private static readonly Dictionary<int, FileStream> OpenFiles = new Dictionary<int, FileStream>();
    private static int nextStreamId = 1;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ParasolidAnalyzer <output-directory> <partition-id|label|x_b-path> [...]");
            return 2;
        }

        string outputDirectory = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outputDirectory);
        PK.SESSION.frustrum_t frustrum = new PK.SESSION.frustrum_t();
        frustrum.fstart = StartCallback;
        frustrum.fabort = AbortCallback;
        frustrum.fstop = StopCallback;
        frustrum.fmallo = AllocateCallback;
        frustrum.fmfree = FreeCallback;
        frustrum.ffoprd = OpenReadCallback;
        frustrum.ffclos = CloseCallback;
        frustrum.ffread = ReadCallback;
        frustrum.ffseek = SeekCallback;
        frustrum.fftell = TellCallback;

        PK.ERROR.code_t status = PK.SESSION.register_frustrum(frustrum);
        if (status != PK.ERROR.code_t.no_errors)
        {
            Console.Error.WriteLine("PK_SESSION_register_frustrum: " + status);
            return 3;
        }

        PK.SESSION.start_o_t startOptions = new PK.SESSION.start_o_t(true);
        status = PK.SESSION.start(&startOptions);
        if (status != PK.ERROR.code_t.no_errors)
        {
            Console.Error.WriteLine("PK_SESSION_start: " + status);
            return 4;
        }

        try
        {
            List<BodyEntry> bodies = new List<BodyEntry>();
            for (int argumentIndex = 1; argumentIndex < args.Length; argumentIndex++)
            {
                LoadPartition(args[argumentIndex], bodies);
            }

            PK.BODY_t referenceSolid = SelectReferenceSolid(bodies);
            WriteBodies(outputDirectory, bodies, referenceSolid);
            WriteSheetGeometry(outputDirectory, bodies, referenceSolid);
            Console.WriteLine("analyzed_bodies=" + bodies.Count.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().FullName + ": " + exception.Message);
            return 5;
        }
        finally
        {
            status = PK.SESSION.stop();
            if (status != PK.ERROR.code_t.no_errors)
            {
                Console.Error.WriteLine("PK_SESSION_stop: " + status);
            }
        }

        return 0;
    }

    private static void LoadPartition(string specification, List<BodyEntry> bodies)
    {
        string[] fields = specification.Split(new[] { '|' }, 3);
        if (fields.Length != 3)
        {
            throw new ArgumentException("Invalid partition specification: " + specification);
        }

        int partitionId;
        if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out partitionId))
        {
            throw new ArgumentException("Invalid partition id: " + fields[0]);
        }

        string path = Path.GetFullPath(fields[2]);
        PK.PART.receive_o_t receiveOptions = new PK.PART.receive_o_t(true);
        int partCount = 0;
        PK.PART_t* parts = null;
        PK.ERROR.code_t status = PK.PART.receive(path, &receiveOptions, &partCount, &parts);
        if (status != PK.ERROR.code_t.no_errors)
        {
            throw new InvalidDataException("PK_PART_receive failed for " + path + ": " + status);
        }

        try
        {
            for (int partIndex = 0; partIndex < partCount; partIndex++)
            {
                PK.CLASS_t entityClass = new PK.CLASS_t();
                status = PK.ENTITY.ask_class((PK.ENTITY_t)parts[partIndex], &entityClass);
                if (status != PK.ERROR.code_t.no_errors || entityClass != PK.CLASS_t.body)
                {
                    continue;
                }

                PK.BODY_t body = (PK.BODY_t)parts[partIndex];
                BodyEntry entry = new BodyEntry();
                entry.PartitionId = partitionId;
                entry.PartitionLabel = fields[1];
                entry.SourcePath = path;
                entry.Body = body;
                PK.BODY.type_t bodyType = new PK.BODY.type_t();
                status = PK.BODY.ask_type(body, &bodyType);
                if (status != PK.ERROR.code_t.no_errors)
                {
                    throw new InvalidDataException("PK_BODY_ask_type failed: " + status);
                }
                entry.BodyType = bodyType;
                entry.FaceCount = AskFaceCount(body);
                entry.EdgeCount = AskEdgeCount(body);
                entry.VertexCount = AskVertexCount(body);
                PK.BOX_t box = new PK.BOX_t();
                status = PK.TOPOL.find_box((PK.TOPOL_t)body, &box);
                if (status != PK.ERROR.code_t.no_errors)
                {
                    throw new InvalidDataException("PK_TOPOL_find_box failed: " + status);
                }
                entry.Box = box;
                bodies.Add(entry);
            }
        }
        finally
        {
            if (parts != null)
            {
                PK.MEMORY.free(parts);
            }
        }
    }

    private static int AskFaceCount(PK.BODY_t body)
    {
        int count = 0;
        PK.FACE_t* faces = null;
        PK.ERROR.code_t status = PK.BODY.ask_faces(body, &count, &faces);
        if (faces != null)
        {
            PK.MEMORY.free(faces);
        }
        return status == PK.ERROR.code_t.no_errors ? count : 0;
    }

    private static int AskEdgeCount(PK.BODY_t body)
    {
        int count = 0;
        PK.EDGE_t* edges = null;
        PK.ERROR.code_t status = PK.BODY.ask_edges(body, &count, &edges);
        if (edges != null)
        {
            PK.MEMORY.free(edges);
        }
        return status == PK.ERROR.code_t.no_errors ? count : 0;
    }

    private static int AskVertexCount(PK.BODY_t body)
    {
        int count = 0;
        PK.VERTEX_t* vertices = null;
        PK.ERROR.code_t status = PK.BODY.ask_vertices(body, &count, &vertices);
        if (vertices != null)
        {
            PK.MEMORY.free(vertices);
        }
        return status == PK.ERROR.code_t.no_errors ? count : 0;
    }

    private static PK.BODY_t SelectReferenceSolid(List<BodyEntry> bodies)
    {
        for (int index = 0; index < bodies.Count; index++)
        {
            BodyEntry body = bodies[index];
            if (body.BodyType == PK.BODY.type_t.solid_c && body.PartitionLabel.IndexOf("tool", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return body.Body;
            }
        }
        for (int index = 0; index < bodies.Count; index++)
        {
            if (bodies[index].BodyType == PK.BODY.type_t.solid_c)
            {
                return bodies[index].Body;
            }
        }
        return new PK.BODY_t();
    }

    private static void WriteBodies(string outputDirectory, List<BodyEntry> bodies, PK.BODY_t referenceSolid)
    {
        string path = Path.Combine(outputDirectory, "bodies.tsv");
        using (StreamWriter writer = NewWriter(path))
        {
            writer.WriteLine("partition_id\tpartition_label\tbody_tag\tbody_type\tface_count\tedge_count\tvertex_count\tmin_x\tmin_y\tmin_z\tmax_x\tmax_y\tmax_z\tis_reference_solid");
            for (int index = 0; index < bodies.Count; index++)
            {
                BodyEntry body = bodies[index];
                PK.BOX_t box = body.Box;
                writer.WriteLine(string.Join("\t", new[]
                {
                    body.PartitionId.ToString(CultureInfo.InvariantCulture),
                    Tsv(body.PartitionLabel),
                    body.Body.Value.ToString(CultureInfo.InvariantCulture),
                    body.BodyType.ToString(),
                    body.FaceCount.ToString(CultureInfo.InvariantCulture),
                    body.EdgeCount.ToString(CultureInfo.InvariantCulture),
                    body.VertexCount.ToString(CultureInfo.InvariantCulture),
                    F(BoxCoord(box, 0)), F(BoxCoord(box, 1)), F(BoxCoord(box, 2)),
                    F(BoxCoord(box, 3)), F(BoxCoord(box, 4)), F(BoxCoord(box, 5)),
                    body.Body.Value == referenceSolid.Value ? "true" : "false"
                }));
            }
        }
    }

    private static void WriteSheetGeometry(string outputDirectory, List<BodyEntry> bodies, PK.BODY_t referenceSolid)
    {
        string facePath = Path.Combine(outputDirectory, "faces.tsv");
        string edgePath = Path.Combine(outputDirectory, "edges.tsv");
        using (StreamWriter faceWriter = NewWriter(facePath))
        using (StreamWriter edgeWriter = NewWriter(edgePath))
        {
            faceWriter.WriteLine("partition_id\tbody_tag\tface_tag\tsurface_class\tmin_x\tmin_y\tmin_z\tmax_x\tmax_y\tmax_z\trange_status\tdistance\tclosest_x\tclosest_y\tclosest_z\tnormal_status\tnormal_x\tnormal_y\tnormal_z\tinterior_x\tinterior_y\tinterior_z");
            edgeWriter.WriteLine("partition_id\tbody_tag\tedge_tag\tcurve_status\tcurve_class\tinterval_status\tlength_status\tlength\tradius_status\tmin_radius\tadjacent_count\tface_a\tface_b\tnormal_status\tnormal_angle_deg\tmidpoint_status\tmid_x\tmid_y\tmid_z\tis_boundary");
            for (int bodyIndex = 0; bodyIndex < bodies.Count; bodyIndex++)
            {
                BodyEntry body = bodies[bodyIndex];
                if (body.BodyType != PK.BODY.type_t.sheet_c)
                {
                    continue;
                }
                Dictionary<int, FaceInfo> faceInfos = WriteFaces(faceWriter, body, referenceSolid);
                WriteEdges(edgeWriter, body, faceInfos);
            }
        }
    }

    private static Dictionary<int, FaceInfo> WriteFaces(StreamWriter writer, BodyEntry body, PK.BODY_t referenceSolid)
    {
        Dictionary<int, FaceInfo> faceInfos = new Dictionary<int, FaceInfo>();
        int faceCount = 0;
        PK.FACE_t* faces = null;
        PK.ERROR.code_t status = PK.BODY.ask_faces(body.Body, &faceCount, &faces);
        if (status != PK.ERROR.code_t.no_errors)
        {
            return faceInfos;
        }

        PK.TOPOL.range_o_t rangeOptions = new PK.TOPOL.range_o_t(true);
        try
        {
            for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
            {
                PK.FACE_t face = faces[faceIndex];
                FaceInfo info = AskFaceInfo(face);
                faceInfos[face.Value] = info;

                string rangeStatus = "unavailable";
                double distance = double.NaN;
                PK.VECTOR_t closest = new PK.VECTOR_t();
                if (referenceSolid.Value != 0)
                {
                    PK.range_result_t rangeResult = new PK.range_result_t();
                    PK.range_2_r_t rangeReport = new PK.range_2_r_t();
                    status = PK.TOPOL.range((PK.TOPOL_t)face, (PK.TOPOL_t)referenceSolid, &rangeOptions, &rangeResult, &rangeReport);
                    rangeStatus = status == PK.ERROR.code_t.no_errors ? rangeResult.ToString() : status.ToString();
                    if (status == PK.ERROR.code_t.no_errors)
                    {
                        distance = rangeReport.distance;
                        closest = rangeReport.endsI0.vector;
                    }
                }

                PK.BOX_t faceBox = info.Box;
                PK.VECTOR_t faceNormal = info.Normal;
                PK.VECTOR_t interiorPoint = info.InteriorPoint;
                writer.WriteLine(string.Join("\t", new[]
                {
                    body.PartitionId.ToString(CultureInfo.InvariantCulture),
                    body.Body.Value.ToString(CultureInfo.InvariantCulture),
                    face.Value.ToString(CultureInfo.InvariantCulture),
                    info.SurfaceClass,
                    F(BoxCoord(faceBox, 0)), F(BoxCoord(faceBox, 1)), F(BoxCoord(faceBox, 2)),
                    F(BoxCoord(faceBox, 3)), F(BoxCoord(faceBox, 4)), F(BoxCoord(faceBox, 5)),
                    rangeStatus, F(distance), F(VectorCoord(closest, 0)), F(VectorCoord(closest, 1)), F(VectorCoord(closest, 2)),
                    info.NormalStatus, F(VectorCoord(faceNormal, 0)), F(VectorCoord(faceNormal, 1)), F(VectorCoord(faceNormal, 2)),
                    F(VectorCoord(interiorPoint, 0)), F(VectorCoord(interiorPoint, 1)), F(VectorCoord(interiorPoint, 2))
                }));
            }
        }
        finally
        {
            if (faces != null)
            {
                PK.MEMORY.free(faces);
            }
        }
        return faceInfos;
    }

    private static FaceInfo AskFaceInfo(PK.FACE_t face)
    {
        FaceInfo info = new FaceInfo();
        info.SurfaceClass = "unknown";
        info.NormalStatus = "unavailable";
        PK.BOX_t box = new PK.BOX_t();
        PK.TOPOL.find_box((PK.TOPOL_t)face, &box);
        info.Box = box;

        PK.SURF_t surface = new PK.SURF_t();
        PK.LOGICAL_t sameSense = new PK.LOGICAL_t();
        PK.ERROR.code_t status = PK.FACE.ask_oriented_surf(face, &surface, &sameSense);
        if (status != PK.ERROR.code_t.no_errors)
        {
            status = PK.FACE.ask_surf(face, &surface);
            sameSense = new PK.LOGICAL_t(1);
        }
        if (status != PK.ERROR.code_t.no_errors)
        {
            info.NormalStatus = status.ToString();
            return info;
        }

        PK.CLASS_t surfaceClass = new PK.CLASS_t();
        status = PK.ENTITY.ask_class((PK.ENTITY_t)surface, &surfaceClass);
        info.SurfaceClass = status == PK.ERROR.code_t.no_errors ? surfaceClass.ToString() : "unknown";

        PK.FACE.find_interior_vec_o_t interiorOptions = new PK.FACE.find_interior_vec_o_t(true);
        PK.UV_t uv = new PK.UV_t();
        PK.VECTOR_t interiorPoint = new PK.VECTOR_t();
        status = PK.FACE.find_interior_vec(face, &interiorOptions, &interiorPoint, &uv);
        if (status != PK.ERROR.code_t.no_errors)
        {
            info.NormalStatus = status.ToString();
            return info;
        }

        info.InteriorPoint = interiorPoint;
        PK.VECTOR_t surfacePoint = new PK.VECTOR_t();
        PK.VECTOR_t normal = new PK.VECTOR_t();
        status = PK.SURF.eval_with_normal(surface, uv, 0, 0, new PK.LOGICAL_t(1), &surfacePoint, &normal);
        info.NormalStatus = status.ToString();
        if (status == PK.ERROR.code_t.no_errors)
        {
            if (sameSense.Value == 0)
            {
                normal.coord[0] = -normal.coord[0];
                normal.coord[1] = -normal.coord[1];
                normal.coord[2] = -normal.coord[2];
            }
            info.Normal = normal;
            info.NormalValid = true;
        }
        return info;
    }

    private static void WriteEdges(StreamWriter writer, BodyEntry body, Dictionary<int, FaceInfo> faceInfos)
    {
        int edgeCount = 0;
        PK.EDGE_t* edges = null;
        PK.ERROR.code_t status = PK.BODY.ask_edges(body.Body, &edgeCount, &edges);
        if (status != PK.ERROR.code_t.no_errors)
        {
            return;
        }

        try
        {
            for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
            {
                PK.EDGE_t edge = edges[edgeIndex];
                PK.CURVE_t curve = new PK.CURVE_t();
                PK.CLASS_t curveClass = new PK.CLASS_t();
                PK.ERROR.code_t curveStatus = PK.EDGE.ask_curve(edge, &curve);
                PK.ERROR.code_t classStatus = curveStatus;
                if (curveStatus == PK.ERROR.code_t.no_errors)
                {
                    classStatus = PK.ENTITY.ask_class((PK.ENTITY_t)curve, &curveClass);
                }

                PK.INTERVAL_t interval = new PK.INTERVAL_t();
                PK.ERROR.code_t intervalStatus = PK.EDGE.find_interval(edge, &interval);
                double length = double.NaN;
                PK.ERROR.code_t lengthStatus = PK.ERROR.code_t.bad_option_data;
                double minRadius = double.NaN;
                PK.ERROR.code_t radiusStatus = PK.ERROR.code_t.bad_option_data;
                PK.VECTOR_t midpoint = new PK.VECTOR_t();
                PK.ERROR.code_t midpointStatus = PK.ERROR.code_t.bad_option_data;

                if (classStatus == PK.ERROR.code_t.no_errors && intervalStatus == PK.ERROR.code_t.no_errors)
                {
                    PK.INTERVAL_t achievedInterval = new PK.INTERVAL_t();
                    lengthStatus = PK.CURVE.find_length(curve, interval, &length, &achievedInterval);
                    double parameter = (IntervalValue(interval, 0) + IntervalValue(interval, 1)) / 2.0;
                    midpointStatus = PK.CURVE.eval(curve, parameter, 0, &midpoint);
                    PK.VECTOR_t radiusPoint = new PK.VECTOR_t();
                    double radiusParameter = 0.0;
                    int radiusCount = 1;
                    radiusStatus = PK.CURVE.find_min_radius(curve, interval, &radiusCount, &minRadius, &radiusPoint, &radiusParameter);
                    if (radiusStatus == PK.ERROR.code_t.no_errors &&
                        (double.IsNaN(minRadius) || double.IsInfinity(minRadius) || minRadius <= 0.0 || minRadius > 1000.0))
                    {
                        minRadius = double.NaN;
                    }
                }
                if (midpointStatus != PK.ERROR.code_t.no_errors)
                {
                    midpointStatus = AskEdgeMidpoint(edge, &midpoint);
                }

                int adjacentCount = 0;
                PK.FACE_t* adjacentFaces = null;
                PK.ERROR.code_t adjacentStatus = PK.EDGE.ask_faces(edge, &adjacentCount, &adjacentFaces);
                int faceA = adjacentStatus == PK.ERROR.code_t.no_errors && adjacentCount > 0 ? adjacentFaces[0].Value : 0;
                int faceB = adjacentStatus == PK.ERROR.code_t.no_errors && adjacentCount > 1 ? adjacentFaces[1].Value : 0;
                if (adjacentFaces != null)
                {
                    PK.MEMORY.free(adjacentFaces);
                }

                double angle = double.NaN;
                string normalStatus = "unavailable";
                FaceInfo infoA;
                FaceInfo infoB;
                if (faceInfos.TryGetValue(faceA, out infoA) && faceInfos.TryGetValue(faceB, out infoB) && infoA.NormalValid && infoB.NormalValid)
                {
                    angle = AcuteNormalAngle(infoA.Normal, infoB.Normal);
                    normalStatus = "no_errors";
                }

                writer.WriteLine(string.Join("\t", new[]
                {
                    body.PartitionId.ToString(CultureInfo.InvariantCulture),
                    body.Body.Value.ToString(CultureInfo.InvariantCulture),
                    edge.Value.ToString(CultureInfo.InvariantCulture),
                    classStatus.ToString(), classStatus == PK.ERROR.code_t.no_errors ? curveClass.ToString() : "unknown",
                    intervalStatus.ToString(), lengthStatus.ToString(), F(length), radiusStatus.ToString(), F(minRadius),
                    adjacentCount.ToString(CultureInfo.InvariantCulture), faceA.ToString(CultureInfo.InvariantCulture), faceB.ToString(CultureInfo.InvariantCulture),
                    normalStatus, F(angle), midpointStatus.ToString(), F(VectorCoord(midpoint, 0)), F(VectorCoord(midpoint, 1)), F(VectorCoord(midpoint, 2)),
                    adjacentCount <= 1 ? "true" : "false"
                }));
            }
        }
        finally
        {
            if (edges != null)
            {
                PK.MEMORY.free(edges);
            }
        }
    }

    private static PK.ERROR.code_t AskEdgeMidpoint(PK.EDGE_t edge, PK.VECTOR_t* midpoint)
    {
        PK.VERTEX_t[] vertices = new PK.VERTEX_t[2];
        PK.ERROR.code_t status = PK.EDGE.ask_vertices(edge, vertices);
        if (status != PK.ERROR.code_t.no_errors)
        {
            return status;
        }

        PK.VECTOR_t first = new PK.VECTOR_t();
        PK.VECTOR_t second = new PK.VECTOR_t();
        status = AskVertexPosition(vertices[0], &first);
        if (status != PK.ERROR.code_t.no_errors)
        {
            return status;
        }
        status = AskVertexPosition(vertices[1], &second);
        if (status != PK.ERROR.code_t.no_errors)
        {
            second = first;
        }
        midpoint->coord[0] = (first.coord[0] + second.coord[0]) / 2.0;
        midpoint->coord[1] = (first.coord[1] + second.coord[1]) / 2.0;
        midpoint->coord[2] = (first.coord[2] + second.coord[2]) / 2.0;
        return PK.ERROR.code_t.no_errors;
    }

    private static PK.ERROR.code_t AskVertexPosition(PK.VERTEX_t vertex, PK.VECTOR_t* position)
    {
        PK.POINT_t point = new PK.POINT_t();
        PK.ERROR.code_t status = PK.VERTEX.ask_point(vertex, &point);
        if (status != PK.ERROR.code_t.no_errors)
        {
            return status;
        }
        PK.POINT_sf_t pointData = new PK.POINT_sf_t();
        status = PK.POINT.ask(point, &pointData);
        if (status == PK.ERROR.code_t.no_errors)
        {
            *position = pointData.position;
        }
        return status;
    }

    private static double AcuteNormalAngle(PK.VECTOR_t first, PK.VECTOR_t second)
    {
        double firstX = VectorCoord(first, 0);
        double firstY = VectorCoord(first, 1);
        double firstZ = VectorCoord(first, 2);
        double secondX = VectorCoord(second, 0);
        double secondY = VectorCoord(second, 1);
        double secondZ = VectorCoord(second, 2);
        double dot = firstX * secondX + firstY * secondY + firstZ * secondZ;
        double firstLength = Math.Sqrt(firstX * firstX + firstY * firstY + firstZ * firstZ);
        double secondLength = Math.Sqrt(secondX * secondX + secondY * secondY + secondZ * secondZ);
        if (firstLength == 0.0 || secondLength == 0.0)
        {
            return double.NaN;
        }
        double cosine = Math.Abs(dot / (firstLength * secondLength));
        cosine = Math.Max(-1.0, Math.Min(1.0, cosine));
        return Math.Acos(cosine) * 180.0 / Math.PI;
    }

    private static double VectorCoord(PK.VECTOR_t vector, int index)
    {
        return vector.coord[index];
    }

    private static double BoxCoord(PK.BOX_t box, int index)
    {
        return box.coord[index];
    }

    private static double IntervalValue(PK.INTERVAL_t interval, int index)
    {
        return interval.value[index];
    }

    private static StreamWriter NewWriter(string path)
    {
        return new StreamWriter(path, false, new UTF8Encoding(false));
    }

    private static string F(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? string.Empty : value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static string Tsv(string value)
    {
        return (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    private static void Start(int* ifail)
    {
        OpenFiles.Clear();
        nextStreamId = 1;
        *ifail = 0;
    }

    private static void Abort(int* ifail)
    {
        *ifail = 0;
    }

    private static void Stop(int* ifail)
    {
        foreach (FileStream stream in OpenFiles.Values)
        {
            stream.Dispose();
        }
        OpenFiles.Clear();
        *ifail = 0;
    }

    private static void Allocate(int* byteCount, byte** memory, int* ifail)
    {
        *memory = (byte*)Marshal.AllocHGlobal(*byteCount);
        *ifail = *memory == null ? 1 : 0;
    }

    private static void Free(int* byteCount, byte** memory, int* ifail)
    {
        if (*memory != null)
        {
            Marshal.FreeHGlobal((IntPtr)(*memory));
            *memory = null;
        }
        *ifail = 0;
    }

    private static void OpenRead(int* guise, int* format, byte* name, int* nameLength, int* skipHeader, int* streamId, int* ifail)
    {
        try
        {
            byte[] nameBytes = new byte[*nameLength];
            Marshal.Copy((IntPtr)name, nameBytes, 0, nameBytes.Length);
            string path = Encoding.Default.GetString(nameBytes);
            FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (*skipHeader == 1)
            {
                SkipHeader(stream);
            }
            int id = nextStreamId++;
            OpenFiles.Add(id, stream);
            *streamId = id;
            *ifail = 0;
        }
        catch (FileNotFoundException)
        {
            *streamId = -1;
            *ifail = 2;
        }
        catch
        {
            *streamId = -1;
            *ifail = 10;
        }
    }

    private static void Close(int* guise, int* streamId, int* action, int* ifail)
    {
        FileStream stream;
        if (!OpenFiles.TryGetValue(*streamId, out stream))
        {
            *ifail = 14;
            return;
        }
        stream.Dispose();
        OpenFiles.Remove(*streamId);
        *ifail = 0;
    }

    private static void Read(int* guise, int* streamId, int* maximumBytes, byte* buffer, int* actualBytes, int* ifail)
    {
        FileStream stream;
        if (!OpenFiles.TryGetValue(*streamId, out stream))
        {
            *actualBytes = 0;
            *ifail = 13;
            return;
        }
        try
        {
            byte[] bytes = new byte[*maximumBytes];
            int count = stream.Read(bytes, 0, bytes.Length);
            if (count > 0)
            {
                Marshal.Copy(bytes, 0, (IntPtr)buffer, count);
            }
            *actualBytes = count;
            *ifail = 0;
        }
        catch
        {
            *actualBytes = 0;
            *ifail = 13;
        }
    }

    private static void Seek(int* guise, int* streamId, int* position, int* ifail)
    {
        FileStream stream;
        if (!OpenFiles.TryGetValue(*streamId, out stream))
        {
            *ifail = 13;
            return;
        }
        stream.Seek(*position, SeekOrigin.Begin);
        *ifail = 0;
    }

    private static void Tell(int* guise, int* streamId, int* position, int* ifail)
    {
        FileStream stream;
        if (!OpenFiles.TryGetValue(*streamId, out stream))
        {
            *ifail = 13;
            return;
        }
        *position = checked((int)stream.Position);
        *ifail = 0;
    }

    private static void SkipHeader(FileStream stream)
    {
        byte[] marker = Encoding.ASCII.GetBytes("**END_OF_HEADER");
        int matched = 0;
        while (true)
        {
            int value = stream.ReadByte();
            if (value < 0)
            {
                throw new InvalidDataException("Parasolid header terminator not found");
            }
            matched = value == marker[matched] ? matched + 1 : value == marker[0] ? 1 : 0;
            if (matched == marker.Length)
            {
                while ((value = stream.ReadByte()) >= 0 && value != '\n')
                {
                }
                return;
            }
        }
    }
}
