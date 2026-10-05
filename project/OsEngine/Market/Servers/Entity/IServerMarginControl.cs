namespace OsEngine.Market.Servers.Entity
{
    /// <summary>
    /// optional interface of a server realization that can change margin mode and leverage of a security on the exchange.
    /// Unlike IServerMarginInfo it WRITES to the exchange account, so a realization must have its own switch
    /// that blocks the changes (on by default)
    /// необязательный интерфейс реализации сервера, который умеет менять режим маржи и плечо инструмента на бирже.
    /// В отличие от IServerMarginInfo он ПИШЕТ в аккаунт биржи, поэтому у реализации должен быть свой выключатель,
    /// блокирующий изменения (по умолчанию включён)
    /// </summary>
    public interface IServerMarginControl
    {
        /// <summary>
        /// set margin mode and leverage on the exchange. Returns false and a reason in message if nothing was done or
        /// only a part was done
        /// выставить режим маржи и плечо на бирже. Возвращает false и причину в message, если ничего не сделано
        /// или сделана только часть
        /// </summary>
        bool SetMarginInfo(string securityNameCode, bool isolated, decimal leverage, out string message);
    }
}
