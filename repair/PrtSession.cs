using System;
using System.IO;
using System.Runtime.InteropServices;
using PK = PLMComponents.Parasolid.PK_.Unsafe;

namespace PartingSurfaceRepair
{
    public static class PrtSession
    {
        private static readonly PK.FSTART.f_t StartCallback = SessionStart;
        private static readonly PK.FABORT.f_t AbortCallback = SessionAbort;
        private static readonly PK.FSTOP.f_t StopCallback = SessionStop;
        private static readonly PK.FMALLO.f_t AllocateCallback = SessionAllocate;
        private static readonly PK.FMFREE.f_t FreeCallback = SessionFree;
        private static readonly PK.FFOPRD.f_t OpenReadCallback = SessionOpenRead;
        private static readonly PK.FFCLOS.f_t CloseCallback = SessionClose;
        private static readonly PK.FFREAD.f_t ReadCallback = SessionRead;
        private static readonly PK.FFSEEK.f_t SeekCallback = SessionSeek;
        private static readonly PK.FFTELL.f_t TellCallback = SessionTell;
        private static readonly System.Collections.Generic.Dictionary<int, FileStream> OpenFiles =
            new System.Collections.Generic.Dictionary<int, FileStream>();
        private static int nextStreamId = 1;
        private static bool sessionStarted;

        public static void Start(string nxRoot)
        {
            if (sessionStarted) return;

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
                throw new InvalidOperationException("PK_SESSION_register_frustrum: " + status);

            PK.SESSION.start_o_t startOptions = new PK.SESSION.start_o_t(true);
            status = PK.SESSION.start(&startOptions);
            if (status != PK.ERROR.code_t.no_errors)
                throw new InvalidOperationException("PK_SESSION_start: " + status);

            sessionStarted = true;
        }

        public static void Stop()
        {
            if (!sessionStarted) return;
            PK.ERROR.code_t status = PK.SESSION.stop();
            if (status != PK.ERROR.code_t.no_errors)
                Console.Error.WriteLine("PK_SESSION_stop: " + status);
            sessionStarted = false;
        }

        public static PK.BODY_t LoadBody(string path)
        {
            PK.PART.receive_o_t receiveOptions = new PK.PART.receive_o_t(true);
            int partCount = 0;
            PK.PART_t* parts = null;
            PK.ERROR.code_t status = PK.PART.receive(path, &receiveOptions, &partCount, &parts);
            if (status != PK.ERROR.code_t.no_errors)
                throw new InvalidDataException("PK_PART_receive failed: " + status);

            PK.BODY_t result = new PK.BODY_t();
            for (int i = 0; i < partCount; i++)
            {
                PK.CLASS_t entityClass = new PK.CLASS_t();
                status = PK.ENTITY.ask_class((PK.ENTITY_t)parts[i], &entityClass);
                if (status != PK.ERROR.code_t.no_errors || entityClass != PK.CLASS_t.body)
                    continue;
                result = (PK.BODY_t)parts[i];
                break;
            }

            if (result == new PK.BODY_t())
                throw new InvalidDataException("No body found in: " + path);

            return result;
        }

        public static void SaveBody(PK.BODY_t body, string outputPath)
        {
            PK.PART.transmit_o_t transmitOptions = new PK.PART.transmit_o_t(true);
            PK.PART_t part = (PK.PART_t)body;
            PK.ERROR.code_t status = PK.PART.transmit(outputPath, &transmitOptions, 1, &part);
            if (status != PK.ERROR.code_t.no_errors)
                throw new InvalidOperationException("PK_PART_transmit failed: " + status);
        }

        public static bool BodyExists(PK.BODY_t body)
        {
            PK.CLASS_t entityClass = new PK.CLASS_t();
            PK.ERROR.code_t status = PK.ENTITY.ask_class((PK.ENTITY_t)body, &entityClass);
            return status == PK.ERROR.code_t.no_errors && entityClass == PK.CLASS_t.body;
        }

        private static int SessionStart(int* nfrustrum, PK.SESSION.frustrum_t* frustrum) => 0;
        private static int SessionAbort(int* nfrustrum, PK.SESSION.frustrum_t* frustrum) => 0;
        private static void SessionStop() { }

        private static unsafe void* SessionAllocate(int* nbytes, int* ifail)
        {
            *ifail = 0;
            return (void*)Marshal.AllocHGlobal(*nbytes);
        }

        private static unsafe void SessionFree(void* memory)
        {
            Marshal.FreeHGlobal((IntPtr)memory);
        }

        private static unsafe void SessionOpenRead(
            int* guise, int* format, byte* name, int* nameLength,
            int* skipHeader, int* streamId, int* ifail)
        {
            *ifail = 0;
            string fileName = System.Text.Encoding.ASCII.GetString(name, *nameLength).TrimEnd('\0');
            if (!File.Exists(fileName)) { *ifail = 1; return; }
            FileStream stream = File.OpenRead(fileName);
            if (*skipHeader == 1) SkipParasolidHeader(stream);
            int id = System.Threading.Interlocked.Increment(ref nextStreamId);
            lock (OpenFiles) { OpenFiles[id] = stream; }
            *streamId = id;
        }

        private static unsafe void SessionClose(int* streamId, int* ifail)
        {
            *ifail = 0;
            lock (OpenFiles)
            {
                if (OpenFiles.TryGetValue(*streamId, out FileStream stream))
                {
                    stream.Dispose();
                    OpenFiles.Remove(*streamId);
                }
            }
        }

        private static unsafe int SessionRead(int* streamId, byte* buffer, int* nbytes, int* ifail)
        {
            *ifail = 0;
            lock (OpenFiles)
            {
                if (!OpenFiles.TryGetValue(*streamId, out FileStream stream)) { *ifail = 1; return 0; }
                return stream.Read(new Span<byte>(buffer, *nbytes));
            }
        }

        private static unsafe void SessionSeek(int* streamId, long* offset, int* whence, int* ifail)
        {
            *ifail = 0;
            lock (OpenFiles)
            {
                if (!OpenFiles.TryGetValue(*streamId, out FileStream stream)) { *ifail = 1; return; }
                SeekOrigin origin = *whence switch { 0 => SeekOrigin.Begin, 1 => SeekOrigin.Current, _ => SeekOrigin.End };
                stream.Seek(*offset, origin);
            }
        }

        private static unsafe long SessionTell(int* streamId, int* ifail)
        {
            *ifail = 0;
            lock (OpenFiles)
            {
                if (!OpenFiles.TryGetValue(*streamId, out FileStream stream)) { *ifail = 1; return 0; }
                return stream.Position;
            }
        }

        private static void SkipParasolidHeader(FileStream stream)
        {
            byte[] marker = System.Text.Encoding.ASCII.GetBytes("**END_OF_HEADER");
            int matched = 0;
            while (matched < marker.Length)
            {
                int b = stream.ReadByte();
                if (b < 0) throw new InvalidDataException("Parasolid header terminator not found");
                matched = ((byte)b == marker[matched]) ? matched + 1 : 0;
            }
        }
    }
}
