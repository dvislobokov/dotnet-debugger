// .NET Framework 4.8 debuggee used by the integration tests (FrameworkTests). Tests locate lines by their
// "// bp:fx_<name>" markers, so code can be moved around freely as long as the markers stay on the right statements.
// Deliberately plain C# 7.3: this is what the code of a real .NET Framework application looks like.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace TestAppFx
{
    class Program
    {
        static void Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "basic";
            Console.WriteLine("mode: " + mode); // bp:fx_entry
            switch (mode)
            {
                case "none":
                    break;
                case "basic":
                    Basic();
                    break;
                case "loop":
                    Loop();
                    break;
                case "exception":
                    CaughtException();
                    break;
                case "unhandled":
                    UnhandledException();
                    break;
                case "threads":
                    Threads();
                    break;
                case "async":
                    int asyncResult = AsyncWork(5).GetAwaiter().GetResult();
                    Console.WriteLine("async result: " + asyncResult);
                    break;
                case "evaluate":
                    Evaluation.Run();
                    break;
                case "output":
                    Output();
                    break;
                case "exit":
                    ExitEarly();
                    break;
                case "wait":
                    Wait();
                    break;
                case "collections":
                    Collections.Run();
                    break;
                case "stdin":
                case "terminal":
                case "zap":
                    LaunchModes.Run(mode);
                    break;
                case "asyncNested":
                    Collections.AsyncNested();
                    break;
                case "domains":
                    Domains.Run(false);
                    break;
                case "domainsShared":
                    Domains.Run(true);
                    break;
                default:
                    Console.Error.WriteLine("unknown mode: " + mode);
                    Environment.ExitCode = 2;
                    return;
            }
            Console.Error.WriteLine("done");
            Environment.ExitCode = 3;
        }

        static void Basic()
        {
            int number = 42;
            string text = "hello \"world\"";
            double ratio = 1.5;
            bool flag = true;
            char letter = 'x';
            decimal money = 12.34m;
            long big = 1234567890123L;
            int? maybe = 7;
            int? nothing = null;
            Color color = Color.Green;
            Access access = Access.Read | Access.Write;
            Point point = new Point { X = 1, Y = 2 };
            Person person = new Person { Name = "Ann", Age = 30, Friend = new Person { Name = "Bob", Age = 31 } };
            Person employee = new Employee { Name = "Eve", Age = 40, Company = "Initech" };
            int[] numbers = new int[] { 10, 20, 30 };
            int[,] grid = { { 1, 2 }, { 3, 4 } };
            List<string> list = new List<string> { "a", "b" };
            Dictionary<string, int> map = new Dictionary<string, int> { { "one", 1 } };
            object boxed = 99;
            Person nobody = null;
            string greeting = "Привет";
            var pair = (Id: 5, Label: "five");

            int sum = Add(number, numbers[0]); // bp:fx_locals
            Console.WriteLine(sum.ToString(CultureInfo.InvariantCulture)); // bp:fx_afterAdd
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "state: {0} {1} {2} {3}", number, text, ratio, flag)); // bp:fx_state
            GC.KeepAlive(new object[] { letter, money, big, maybe, nothing, color, access, point, person, employee, grid, list, map, boxed, nobody, greeting, pair });
        }

        static int Add(int a, int b)
        {
            int result = a + b; // bp:fx_add
            return result;
        }

        static void Loop()
        {
            int total = 0;
            for (int i = 0; i < 10; i++)
            {
                total += i; // bp:fx_loopBody
            }
            Console.WriteLine("total: " + total); // bp:fx_loopEnd
        }

        static void CaughtException()
        {
            try
            {
                throw new InvalidOperationException("caught one"); // bp:fx_throwCaught
            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine("handled: " + e.Message); // bp:fx_catch
            }
        }

        static void UnhandledException()
        {
            Fail(new FormatException("inner cause"));
        }

        static void Fail(Exception inner)
        {
            throw new ArgumentException("fatal one", inner); // bp:fx_throwFatal
        }

        static void Threads()
        {
            Thread worker = new Thread(WorkerBody);
            worker.Name = "fx-worker";
            worker.Start();
            worker.Join();
        }

        static void WorkerBody()
        {
            int workerValue = 11;
            Console.WriteLine("worker running " + workerValue); // bp:fx_worker
        }

        static async Task<int> AsyncWork(int input)
        {
            int doubled = input * 2; // bp:fx_asyncStart
            await Task.Delay(10);
            int final = doubled + 1; // bp:fx_async
            return final;
        }

        static void Output()
        {
            Console.WriteLine("plain ascii line");
            Console.WriteLine("Привет, мир! Grüße €"); // bp:fx_output
            Console.Error.WriteLine("error line: Ошибка");
        }

        static void ExitEarly()
        {
            Console.WriteLine("exiting with 5");
            Environment.Exit(5); // bp:fx_exit
        }

        static void Wait()
        {
            int counter = 0;
            while (counter >= 0)
            {
                Thread.Sleep(20);
                counter++; // bp:fx_waitLoop
            }
        }
    }
}
