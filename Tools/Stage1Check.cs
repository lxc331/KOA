using System;

internal static class Stage1Check
{
    public static int Main()
    {
        try
        {
            RehabPhotoGame.Editor.StageOneTests.RunLogicTests();
            RehabPhotoGame.Editor.StageThreeTests.RunLogicTests();
            RehabPhotoGame.Editor.StageFourTests.RunLogicTests();
            Console.WriteLine("PASS: 49 pure logic scenarios. Unity-native integration tests require the Editor.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
