﻿namespace ALog.Public.Interfaces;

public interface ILogFilter
{
    bool ShouldLog(LogEvent logEvent);
}