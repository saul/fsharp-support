﻿using static Module;

public class Class1
{
  public static T M<T>(T a) => default;

  public Class1()
  {
    D<string> d1 = M;
    string r1 = d1("");

    D<string> d2 = f;
    string r2 = d2("");
  }
}
