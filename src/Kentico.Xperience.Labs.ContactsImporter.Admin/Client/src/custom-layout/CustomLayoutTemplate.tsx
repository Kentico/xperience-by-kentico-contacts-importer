import React, { useState } from "react";
import { Button, ButtonSize } from "@kentico/xperience-admin-components";
import { usePageCommand } from "@kentico/xperience-admin-base";

/*
* This file demonstrates a custom UI page template.
  The template supports a single page command that retrieves a string value from the backend.

  In this example, the command retrieves the server's DateTime.Now value and displays it in a label.
  See ~\UIPages\CustomTemplate\CustomTemplate.cs for the backend definition of the page.
*/

interface CustomLayoutProps {
  readonly label: string;
}

interface ResponseResult {
  readonly label: string;
}

const Commands = {
  SetLabel: "SetLabel",
};

export const CustomLayoutTemplate = ({ label }: CustomLayoutProps) => {
  const [labelValue, setLabelValue] = useState(label);

  const { execute: submit } = usePageCommand<ResponseResult>(
    Commands.SetLabel,
    {
      after: (response) => {
         if (response) {
          setLabelValue(response.label);
        }
      },
    }
  );

  return (
    <div>
      <h1>{labelValue}</h1>
      <Button
        label="Get server time"
        size={ButtonSize.S}
        onClick={() => submit()}
      />
    </div>
  );
};
