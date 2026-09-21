Console.WriteLine("async main start"); // bp:asyncMainEntry
await Task.Delay(10);
Console.WriteLine("async main done");
return 0;
