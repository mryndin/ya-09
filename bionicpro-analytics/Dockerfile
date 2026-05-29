FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Копируем csproj и восстанавливаем зависимости
COPY ["bionicpro-analytics.csproj", "./"]
RUN dotnet restore "bionicpro-analytics.csproj"

# Копируем оставшийся исходный код и собираем релиз
COPY . .
RUN dotnet publish "bionicpro-analytics.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Финальный легковесный образ для рантайма
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .

# Выставляем стандартный порт ASP.NET внутри сети Docker
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "bionicpro-analytics.dll"]