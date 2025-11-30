## DbMetaTool to narzędzie konsolowe, które umożliwia:

- tworzenie nowej bazy Firebird
- eksport metadanych (domeny, tabele, procedury) do plików .sql + metadata.json
- aktualizowanie istniejącej bazy na podstawie podanych skryptów SQL

## Funkcje: 

- build-db
  - Tworzy nową bazę danych na podstawie skryptów SQL : domains.sql, tables.sql oraz procedures.sql

- export-scripts
  - Eksportuje metadane z istniejącej bazy Firebird 5.0 do plików: domains.sql, tables.sql, procedures.sql oraz metadata.json
  
- update-db
  - Aktualizuje istniejącą bazę danych, wykonując skrypty SQL.
 
## Uruchomienie:

- ``dotnet publish -c Release -r win-x64 --self-contained true``
- ``cd bin\Release\net8.0\win-x64\publish``
  - Aby uruchomić export skryptów: ``  export-scripts --connection-string <connStr> --output-dir <ścieżka>``
  - Aby uruchomić tworzenie bazy danych: ``  build-db --db-dir <ścieżka> --scripts-dir <ścieżka>``
  - Aby uruchomić aktualizację bazy danych: ``  update-db --connection-string <connStr> --scripts-dir <ścieżka>``

  *W przypadku podania nieistniejącej ścieżki tworzenia bazy, program sam utworzy podaną ścieżkę!

