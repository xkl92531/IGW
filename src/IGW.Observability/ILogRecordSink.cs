namespace IGW.Observability;

public interface ILogRecordSink
{
    void Append(LogRecord record);
}
