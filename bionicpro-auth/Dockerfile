FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["bionicpro-auth.csproj", "./"]
RUN dotnet restore "bionicpro-auth.csproj"
COPY . .
RUN dotnet publish "bionicpro-auth.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
# Было: ENTRYPOINT ["dotnet", "BionicProAuth.dll"]
ENTRYPOINT ["dotnet", "bionicpro-auth.dll"]